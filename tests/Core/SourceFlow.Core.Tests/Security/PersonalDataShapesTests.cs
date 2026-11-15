using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Catalog;
using SourceFlow.Core.Tests.Runtime;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Security;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;

namespace SourceFlow.Core.Tests.Security;

/// <summary>
/// UT-SEC-30..35 (WP-C.3, doc 10 §4): personal data written the way doc 10 shows it (a positional record parameter), inside
/// collections, shapes encryption can't follow (fail closed), erasure across nodes (bounded key cache), the erasure audit
/// and the production guard on <see cref="LocalKeyProvider"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PersonalDataShapesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-SEC-30")]
    [Trait("Covers", "doc 10 4;doc 10 5")]
    public async Task PersonalData_OnAPositionalParameter_IsEncryptedClassifiedAndRedacted()
    {
        using var keys = new LocalKeyProvider();
        RuntimeModel model = Model();
        using var protector = new PersonalDataProtector(keys, new InMemoryStore(), model);
        byte[] json = Serialize(model, new PositionalVisit("cust-1", "Ada Lovelace", "London"));

        (ReadOnlyMemory<byte> body, IReadOnlyCollection<string> subjects) = await protector.ProtectAsync(typeof(PositionalVisit), "-", "visit-1", json, Ct);

        string stored = Encoding.UTF8.GetString(body.Span);
        stored.ShouldNotContain("Ada Lovelace");
        stored.ShouldContain("\"$pd\":\"v2:");
        stored.ShouldContain("\"city\":\"London\"");
        subjects.ShouldBe(["cust-1"]);
        Encoding.UTF8.GetString((await protector.UnprotectAsync(body, Ct)).Span).ShouldContain("Ada Lovelace");

        // The query catalog and the schema see it as personal data too (SFQ0301, x-sourceflow-personal-data).
        QueryCatalog.Build(model).EventTypes.Single(e => e.Name == "test.shapes.visit").Latest.Fields.Single(f => f.Name == "name").PersonalData.ShouldBeTrue();
        SchemaCatalog.Build(model).Entries.Single(e => e.Type == "test.shapes.visit").Canonical.ShouldContain("\"x-sourceflow-personal-data\":true");

        // Logs redact it when payloads are logged.
        var services = new ServiceCollection();
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "shapes";
            sf.AddModule<ShapesModule>();
            sf.UseInMemoryStorage();
            sf.Configure(o => o.LogPayloads = true);
        });
        await using ServiceProvider provider = services.BuildServiceProvider();
        SourceFlowRuntime runtime = provider.GetRequiredService<SourceFlowRuntime>();
        string logged = provider.GetRequiredService<PayloadRedaction>().ForLog(runtime.CreateEnvelope(new PositionalVisit("cust-1", "Ada Lovelace", "London")));
        logged.ShouldNotContain("Ada Lovelace");
        logged.ShouldContain("London");
    }

    [Fact]
    [Trait("Id", "UT-SEC-31")]
    [Trait("Covers", "doc 10 4")]
    public async Task PersonalData_InsideCollections_IsEncryptedPerElement_AndErasedPerSubject()
    {
        using var keys = new LocalKeyProvider();
        var store = new InMemoryStore();
        RuntimeModel model = Model();
        using var protector = new PersonalDataProtector(keys, store, model);
        var basket = new Basket("cust-1",
            [new BasketLine("sku-1", "Ada Lovelace"), new BasketLine("sku-2", "Grace Hopper")],
            [new GiftNote("owner-a", "For Ada"), new GiftNote("owner-b", "For Grace")]);

        (ReadOnlyMemory<byte> body, IReadOnlyCollection<string> subjects) = await protector.ProtectAsync(typeof(Basket), "-", "basket-1", Serialize(model, basket), Ct);

        string stored = Encoding.UTF8.GetString(body.Span);
        foreach (string plain in new[] { "Ada Lovelace", "Grace Hopper", "For Ada", "For Grace" })
        {
            stored.ShouldNotContain(plain);
        }

        stored.ShouldContain("sku-1");
        subjects.ShouldBe(["cust-1", "owner-a", "owner-b"], ignoreOrder: true);   // the outer subject, and each element's own

        JsonNode restored = JsonNode.Parse((await protector.UnprotectAsync(body, Ct)).Span)!;
        restored["lines"]![1]!["recipient"]!.GetValue<string>().ShouldBe("Grace Hopper");
        restored["notes"]![0]!["text"]!.GetValue<string>().ShouldBe("For Ada");

        // Erasing one element's subject erases that element only.
        await store.ShredKeyAsync("-", "owner-a", TimeProvider.System.GetUtcNow(), Ct);
        protector.Evict("-", "owner-a");
        JsonNode erased = JsonNode.Parse((await protector.UnprotectAsync(body, Ct)).Span)!;
        erased["notes"]![0]!["text"]!.GetValue<string>().ShouldBe(PersonalDataProtector.Erased);
        erased["notes"]![1]!["text"]!.GetValue<string>().ShouldBe("For Grace");
        erased["lines"]![0]!["recipient"]!.GetValue<string>().ShouldBe("Ada Lovelace");

        // Scrubbing reaches into collections as well.
        JsonNode scrubbed = JsonNode.Parse(protector.Scrub(typeof(Basket), Serialize(model, basket)).Span)!;
        scrubbed["lines"]![0]!["recipient"]!.GetValue<string>().ShouldBe(PersonalDataProtector.Erased);
        scrubbed["notes"]![1]!["text"]!.GetValue<string>().ShouldBe(PersonalDataProtector.Erased);
    }

    [Fact]
    [Trait("Id", "UT-SEC-32")]
    [Trait("Covers", "doc 10 4")]
    public void PersonalData_InAShapeEncryptionCantFollow_FailsAtStartup()
    {
        using var keys = new LocalKeyProvider();
        using var protector = new PersonalDataProtector(keys, new InMemoryStore(), new SourceFlowModelBuilder().AddModule<ShapesModule>().AddModule<LedgerModule>().Build());

        Should.Throw<InvalidOperationException>(() => protector.Validate([typeof(Ledger)])).Message.ShouldContain("dictionary");

        var services = new ServiceCollection();
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "shapes";
            sf.AddModule<ShapesModule>();
            sf.AddModule<LedgerModule>();
            sf.UseInMemoryStorage();
            sf.Security(s => s.UseKeyProvider(keys).EncryptPersonalData());
        });
        using ServiceProvider provider = services.BuildServiceProvider();
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<SourceFlowRuntime>()).Message.ShouldContain("Ledger");
    }

    [Fact]
    [Trait("Id", "UT-SEC-33")]
    [Trait("Covers", "doc 10 1;doc 10 8")]
    public async Task Erasure_RecordsWhoErasedTheSubject()
    {
        using var keys = new LocalKeyProvider();
        var store = new InMemoryStore();
        var services = new ServiceCollection();
        services.AddSingleton<IActorAccessor>(new FixedActor("dpo:alice"));
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "clinic";
            sf.AddModule<CoreTestModule>();
            sf.UseInMemoryStorage(store);
            sf.Security(s => s.UseKeyProvider(keys).EncryptPersonalData());
        });
        await using ServiceProvider provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IPersonalDataEraser>().EraseSubjectAsync("cust-9", cancellationToken: Ct);

        StreamSlice erasure = await ((IEventStore)store).ReadStreamAsync("-", new StreamId("personal-data-cust-9"), new StreamVersion(1), 10, Ct);
        erasure.Events.ShouldHaveSingleItem().Metadata.Actor.ShouldBe("dpo:alice");
    }

    [Fact]
    [Trait("Id", "UT-SEC-34")]
    [Trait("Covers", "doc 10 4")]
    public async Task Erasure_OnAnotherNode_TakesEffectHereWithinTheKeyCacheLifetime()
    {
        using var keys = new LocalKeyProvider();
        var store = new InMemoryStore();   // the shared database
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        RuntimeModel model = Model();
        using var nodeA = new PersonalDataProtector(keys, store, model, time, TimeSpan.FromMinutes(2));
        using var nodeB = new PersonalDataProtector(keys, store, model, time, TimeSpan.FromMinutes(2));
        (ReadOnlyMemory<byte> body, _) = await nodeA.ProtectAsync(typeof(PositionalVisit), "-", "visit-1", Serialize(model, new PositionalVisit("cust-1", "Ada Lovelace", "London")), Ct);
        Encoding.UTF8.GetString((await nodeB.UnprotectAsync(body, Ct)).Span).ShouldContain("Ada Lovelace");   // node B caches the key

        // Node A erases: it evicts its own cache; node B still holds the key until its cache entry expires.
        await store.ShredKeyAsync("-", "cust-1", time.GetUtcNow(), Ct);
        nodeA.Evict("-", "cust-1");
        time.Advance(TimeSpan.FromMinutes(1));
        Encoding.UTF8.GetString((await nodeB.UnprotectAsync(body, Ct)).Span).ShouldContain("Ada Lovelace", customMessage: "within the lifetime");
        time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));

        Encoding.UTF8.GetString((await nodeB.UnprotectAsync(body, Ct)).Span).ShouldContain(PersonalDataProtector.Erased);
        Should.Throw<ArgumentOutOfRangeException>(() => new PersonalDataProtector(keys, store, model, time, TimeSpan.Zero));
    }

    [Theory]
    [Trait("Id", "UT-SEC-35")]
    [Trait("Covers", "ADR-027;doc 10 3")]
    [InlineData("Production", false, false)]
    [InlineData("Production", true, true)]
    [InlineData("Development", false, true)]
    public async Task LocalKeyProvider_OutsideDevelopment_NeedsAnExplicitAllowance(string environment, bool allow, bool starts)
    {
        using var keys = new LocalKeyProvider();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment, DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "clinic";
            sf.AddModule<CoreTestModule>();
            sf.UseInMemoryStorage();
            sf.Security(s =>
            {
                s.UseKeyProvider(keys).EncryptPersonalData();
                if (allow)
                {
                    s.AllowLocalKeyProvider();
                }
            });
        });
        using IHost host = builder.Build();

        if (starts)
        {
            await host.StartAsync(Ct);
            await host.StopAsync(Ct);
        }
        else
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync(Ct))).Message.ShouldContain("AllowLocalKeyProvider");
        }
    }

    private static RuntimeModel Model()
    {
        return new SourceFlowModelBuilder().AddModule<ShapesModule>().Build();
    }

    private static byte[] Serialize(RuntimeModel model, object value)
    {
        model.TryGetJsonTypeInfo(value.GetType(), out System.Text.Json.Serialization.Metadata.JsonTypeInfo? info).ShouldBeTrue();
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, info!);
    }

    private sealed class FixedActor(string actor) : IActorAccessor
    {
        public string? Actor => actor;
    }
}

// Hand-registered message types (ShapesModule), so no [MessageType]: the generated CoreTestModule must not pick them up.
#pragma warning disable SF0001

// Written as doc 10 §4 shows it: the attribute on the positional parameter, no [property:] target.
public sealed record PositionalVisit(string Customer, [PersonalData(Subject = nameof(PositionalVisit.Customer))] string Name, string City) : IEvent;

public sealed record BasketLine(string Sku, [property: PersonalData(Subject = "Customer")] string Recipient);

public sealed record GiftNote(string Owner, [property: PersonalData(Subject = nameof(GiftNote.Owner))] string Text);

public sealed record Basket(string Customer, IReadOnlyList<BasketLine> Lines, IReadOnlyList<GiftNote> Notes) : IEvent;

public sealed record LedgerEntry([property: PersonalData] string Payee);

public sealed record Ledger(IReadOnlyDictionary<string, LedgerEntry> Entries) : IEvent;
#pragma warning restore SF0001

[System.Text.Json.Serialization.JsonSerializable(typeof(PositionalVisit))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Basket))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
public sealed partial class ShapesJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

[System.Text.Json.Serialization.JsonSerializable(typeof(Ledger))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
public sealed partial class LedgerJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

// Registered by hand (no [MessageType]), so the generated CoreTestModule doesn't pick these types up.
public sealed class ShapesModule : ISourceFlowModule
{
    public static string ModuleName => "ShapesModule";

    public static void Register(ISourceFlowModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddJsonTypeInfoResolver(ShapesJsonContext.Default);
        builder.AddMessage<PositionalVisit>("test.shapes.visit", 1);
        builder.AddMessage<Basket>("test.shapes.basket", 1);
    }
}

public sealed class LedgerModule : ISourceFlowModule
{
    public static string ModuleName => "LedgerModule";

    public static void Register(ISourceFlowModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddJsonTypeInfoResolver(LedgerJsonContext.Default);
        builder.AddMessage<Ledger>("test.shapes.ledger", 1);
    }
}
