using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

// Version 2 of test-drips keeps every value. It is registered by hand (no [Projection]), so CoreTestModule keeps carrying
// only version 1 and the other tests are unaffected.
public sealed record DripsV2(DripId Id, IReadOnlyList<string> Values);

public sealed class DripsV2Projection : IDocumentProjectionRuntime<DripsV2Projection, DripsV2, DripId>
{
    private static readonly Type[] Handled = [typeof(Dripped)];

    public static string ProjectionName => "test-drips";

    public static int ProjectionVersion => 2;

    public static IReadOnlyList<Type> HandledEvents => Handled;

    public static bool TryGetKey(IEvent @event, in EventContext context, out DripId key)
    {
        if (@event is Dripped e)
        {
            key = e.DripId;
            return true;
        }

        key = default;
        return false;
    }

    public static string FormatKey(DripId key)
    {
        return key.ToStreamSegment();
    }

    public DripsV2? Apply(IEvent @event, DripsV2? current, in EventContext context)
    {
        return @event is Dripped e ? new DripsV2(e.DripId, [.. current?.Values ?? [], e.Value]) : current;
    }
}

[JsonSerializable(typeof(DripsV2))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class DripsV2JsonContext : JsonSerializerContext;

public sealed class DripsV2Module : ISourceFlowModule
{
    public static string ModuleName => "DripsV2Module";

    public static void Register(ISourceFlowModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddJsonTypeInfoResolver(DripsV2JsonContext.Default);
        builder.AddProjection<DripsV2Projection, DripsV2, DripId>();
    }
}

/// <summary>UT-SUB-25..27 and UT-GEN (plan 05 §3.7, WP-2.7): blue/green read-model generations (doc 06 §6).</summary>
[Trait("Category", "Unit")]
public sealed class GenerationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Id", "UT-GEN-01")]
    [InlineData("account-summary@1", "account-summary", 1, 1)]
    [InlineData("account-summary@2.3", "account-summary", 2, 3)]
    [InlineData("a@b@12", "a@b", 12, 1)]
    public void Generation_ParsesAndFormatsCollectionIds(string text, string name, int version, int number)
    {
        ReadModelGeneration generation = ReadModelGeneration.Parse(text);

        generation.ShouldBe(new ReadModelGeneration(name, version, number));
        generation.Collection.ShouldBe(text);
        generation.Subscription.ShouldBe("projection:" + text);
    }

    [Theory]
    [Trait("Id", "UT-GEN-02")]
    [InlineData("")]
    [InlineData("summary")]
    [InlineData("@1")]
    [InlineData("summary@")]
    [InlineData("summary@0")]
    [InlineData("summary@1.1")]
    [InlineData("summary@1.x")]
    [InlineData("summary@-1")]
    public void Generation_RejectsMalformedIds(string text)
    {
        ReadModelGeneration.TryParse(text, out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-GEN-03")]
    public void Generations_AreOrderedByVersionThenNumber()
    {
        var first = new ReadModelGeneration("s", 1, 1);

        first.Next().ShouldBe(new ReadModelGeneration("s", 1, 2));
        first.Next().Collection.ShouldBe("s@1.2");
        new ReadModelGeneration("s", 2, 1).IsNewerThan(first.Next()).ShouldBeTrue();
        first.Next().IsNewerThan(first).ShouldBeTrue();
        first.IsNewerThan(first).ShouldBeFalse();
        new ReadModelGeneration("other", 9, 1).IsNewerThan(first).ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-GEN-04")]
    public async Task Aliases_AreCompareAndSet()
    {
        var store = new InMemoryStore();

        (await store.SwitchAsync("s", null, "s@1", Ct)).ShouldBeTrue();
        (await store.SwitchAsync("s", null, "s@1", Ct)).ShouldBeFalse();      // already claimed
        await store.RequestAsync("s", "s@1", "s@1.2", Ct);
        (await store.SwitchAsync("s", "s@0", "s@1.2", Ct)).ShouldBeFalse();  // stale expectation
        (await store.SwitchAsync("s", "s@1", "s@1.2", Ct)).ShouldBeTrue();

        CollectionAlias alias = (await store.ListAliasesAsync(Ct)).ShouldHaveSingleItem();
        alias.Target.ShouldBe("s@1.2");
        alias.Previous.ShouldBe("s@1");
        alias.Building.ShouldBeNull();
        await store.ClearPreviousAsync("s", "s@0", Ct);
        (await store.ListAliasesAsync(Ct))[0].Previous.ShouldBe("s@1");
        await store.ClearPreviousAsync("s", "s@1", Ct);
        (await store.ListAliasesAsync(Ct))[0].Previous.ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "UT-GEN-05")]
    public void TwoVersionsOfAProjection_InOneService_FailAtStartup()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() => services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "drips";
            sf.AddModule<CoreTestModule>();
            sf.AddModule<DripsV2Module>();
            sf.UseInMemoryStorage();
        })).Message.ShouldContain("share the name 'test-drips'");
    }

    [Fact]
    [Trait("Id", "UT-SUB-25")]
    [Trait("Id", "UT-SUB-26")]
    [Trait("Covers", "doc 06 6;ADR-032")]
    public async Task VersionBump_BuildsTheNewGenerationFromTheStart_ThenSwitchesReads()
    {
        var store = new InMemoryStore();
        DripId first = DripId.New();
        DripId second = DripId.New();
        await using (SourceFlowTestHost v1 = await StartAsync(store, version: 1))
        {
            await v1.SendAsync(new Drip(first, "a"), cancellationToken: Ct);
            await v1.SendAsync(new Drip(first, "b"), cancellationToken: Ct);
            await v1.SendAsync(new Drip(second, "c"), cancellationToken: Ct);
            await v1.WaitForIdleAsync();
            await EventuallyAsync(store, a => a.Target == "test-drips@1");   // the first generation claims the alias
        }

        await using SourceFlowTestHost v2 = await StartAsync(store, version: 2);
        await v2.WaitForIdleAsync();

        CollectionAlias alias = (await store.ListAliasesAsync(Ct)).ShouldHaveSingleItem();
        alias.Target.ShouldBe("test-drips@2");
        alias.Previous.ShouldBe("test-drips@1");
        (await v2.DocumentAsync<DripsV2Projection, DripsV2>(first.ToStreamSegment()))!.Values.ShouldBe(["a", "b"]);   // replayed from position zero
        (await v2.Services.GetRequiredService<IReadModelResolver>().ResolveAsync("test-drips", Ct)).ShouldBe(new ReadModelGeneration("test-drips", 2, 1));

        // Queries read the alias: the version 2 documents, not the retained version 1 ones.
        IReadOnlyList<QueryRow> rows = await v2.Services.GetRequiredService<IQueryService>()
            .QueryAsync("from readmodel \"test-drips\" | project id, values | order by id", cancellationToken: Ct).ToListAsync(Ct);
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.Values[1] is JsonElement);
        (await ((IDocumentReader)store).GetAsync(MessageEnvelope.DefaultTenant, "test-drips@1", first.ToStreamSegment(), Ct)).ShouldNotBeNull();   // kept until RetainPreviousFor
    }

    [Fact]
    [Trait("Id", "UT-SUB-26")]
    [Trait("Covers", "doc 06 6")]
    public async Task RollingDeploy_TheOldVersionStopsOnceTheNewOneServesReads()
    {
        var store = new InMemoryStore();
        DripId before = DripId.New();
        DripId after = DripId.New();
        await using SourceFlowTestHost v1 = await StartAsync(store, version: 1);
        await v1.SendAsync(new Drip(before, "a"), cancellationToken: Ct);
        await v1.WaitForIdleAsync();

        await using SourceFlowTestHost v2 = await StartAsync(store, version: 2);
        await SourceFlowTestHost.WaitForIdleAsync([v1, v2]);
        await EventuallyAsync(store, a => a.Target == "test-drips@2");
        SubscriptionEngine old = v1.Services.GetRequiredService<SubscriptionEngine>();
        for (int i = 0; i < 500 && old.Definitions.Any(d => d.Name == "projection:test-drips@1"); i++)
        {
            await v1.WaitForIdleAsync();   // passes refresh the aliases; the old node retires @1 within a poll interval
        }

        old.Definitions.ShouldNotContain(d => d.Name == "projection:test-drips@1");
        await v1.SendAsync(new Drip(after, "b"), cancellationToken: Ct);   // an old-version node still takes commands
        await SourceFlowTestHost.WaitForIdleAsync([v1, v2]);

        var reader = (IDocumentReader)store;
        (await reader.GetAsync(MessageEnvelope.DefaultTenant, "test-drips@2", after.ToStreamSegment(), Ct)).ShouldNotBeNull();
        (await reader.GetAsync(MessageEnvelope.DefaultTenant, "test-drips@1", after.ToStreamSegment(), Ct)).ShouldBeNull();   // @1 is retired
        (await v1.Services.GetRequiredService<IReadModelResolver>().ResolveAsync("test-drips", Ct)).Version.ShouldBe(2);
    }

    [Fact]
    [Trait("Id", "UT-SUB-27")]
    [Trait("Covers", "doc 06 6")]
    public async Task RetiredGeneration_IsDroppedAfterRetainPreviousFor()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var store = new InMemoryStore(time);
        DripId id = DripId.New();
        await using (SourceFlowTestHost v1 = await StartAsync(store, version: 1, time: time))
        {
            await v1.SendAsync(new Drip(id, "a"), cancellationToken: Ct);
            await v1.WaitForIdleAsync();
            await EventuallyAsync(store, a => a.Target == "test-drips@1");
        }

        await using SourceFlowTestHost v2 = await StartAsync(store, version: 2, time: time);
        await v2.WaitForIdleAsync();
        await EventuallyAsync(store, a => a.Previous == "test-drips@1");
        var reader = (IDocumentReader)store;
        time.Advance(TimeSpan.FromDays(1));
        await v2.WaitForIdleAsync();
        (await reader.GetAsync(MessageEnvelope.DefaultTenant, "test-drips@1", id.ToStreamSegment(), Ct)).ShouldNotBeNull();   // within RetainPreviousFor

        time.Advance(TimeSpan.FromDays(2));
        await EventuallyAsync(store, a => a.Previous is null, v2);
        (await reader.GetAsync(MessageEnvelope.DefaultTenant, "test-drips@1", id.ToStreamSegment(), Ct)).ShouldBeNull();
        (await reader.GetAsync(MessageEnvelope.DefaultTenant, "test-drips@2", id.ToStreamSegment(), Ct)).ShouldNotBeNull();
    }

    [Fact]
    [Trait("Id", "UT-GEN-06")]
    [Trait("Covers", "doc 06 6")]
    public async Task RequestedRebuild_BuildsTheNextGenerationOfTheSameVersion()
    {
        var store = new InMemoryStore();
        DripId id = DripId.New();
        await using SourceFlowTestHost host = await StartAsync(store, version: 1);
        await host.SendAsync(new Drip(id, "a"), cancellationToken: Ct);
        await host.WaitForIdleAsync();
        await EventuallyAsync(store, a => a.Target == "test-drips@1");

        await store.RequestAsync("test-drips", "test-drips@1", "test-drips@1.2", Ct);
        await EventuallyAsync(store, a => a.Target == "test-drips@1.2", host);

        (await host.DocumentAsync<DripsProjection, Drips>(id.ToStreamSegment()))!.Count.ShouldBe(1);
        (await ((IDocumentReader)store).GetAsync(MessageEnvelope.DefaultTenant, "test-drips@1.2", id.ToStreamSegment(), Ct)).ShouldNotBeNull();
        (await store.ListAliasesAsync(Ct))[0].Building.ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "UT-GEN-07")]
    [Trait("Covers", "doc 06 6")]
    public async Task SwitchLagThreshold_HoldsTheSwitchUntilTheNewGenerationIsCloseEnough()
    {
        var store = new InMemoryStore();
        await using (SourceFlowTestHost v1 = await StartAsync(store, version: 1))
        {
            for (int i = 0; i < 5; i++)
            {
                await v1.SendAsync(new Drip(DripId.New(), "x"), cancellationToken: Ct);
            }

            await v1.WaitForIdleAsync();
        }

        // No loops: the test drives the engine, with a batch size that leaves the new generation 3 events behind.
        await using SourceFlowTestHost v2 = await StartAsync(store, version: 2, configure: o =>
        {
            o.SwitchLagThreshold = 2;
            o.SubscriptionBatchSize = 2;
        }, loops: false);
        SubscriptionEngine engine = v2.Services.GetRequiredService<SubscriptionEngine>();
        (await engine.RunOnceAsync(Ct)).ShouldBe(2);   // 2 of 5 applied: 3 behind, above the threshold
        (await store.ListAliasesAsync(Ct))[0].Target.ShouldBe("test-drips@1");
        for (int pass = 0; pass < 4; pass++)
        {
            await engine.RunOnceAsync(Ct);   // catches up; an idle pass checks the lag and switches
        }

        (await store.ListAliasesAsync(Ct))[0].Target.ShouldBe("test-drips@2");
    }

    private static Task<SourceFlowTestHost> StartAsync(InMemoryStore store, int version, TimeProvider? time = null, Action<SourceFlowOptions>? configure = null, bool loops = true)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "drips";
            sf.AddModule<CoreTestModule>();
            sf.AddModule<DripsV2Module>();
            sf.Projections(p =>
            {
                if (version == 1)
                {
                    p.Remove<DripsV2Projection>();
                    p.Add<DripsProjection>().Async();
                }
                else
                {
                    p.Remove<DripsProjection>();
                    p.Add<DripsV2Projection>().Async();
                }
            });
            sf.Configure(o =>
            {
                o.PollInterval = TimeSpan.FromMilliseconds(20);
                o.RetainPreviousFor = TimeSpan.FromDays(2);
                o.RetentionInterval = TimeSpan.FromMinutes(1);
                configure?.Invoke(o);
            });
            if (!loops)
            {
                sf.Roles = SourceFlowRoles.Api;
            }
        }, time: time, store: store);
    }

    private static async Task EventuallyAsync(InMemoryStore store, Func<CollectionAlias, bool> condition, SourceFlowTestHost? pulse = null)
    {
        for (int i = 0; i < 500; i++)
        {
            if ((await store.ListAliasesAsync(Ct)).FirstOrDefault() is { } alias && condition(alias))
            {
                return;
            }

            if (pulse is not null)
            {
                await pulse.WaitForIdleAsync();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }

        throw new TimeoutException("The alias did not reach the expected state: " + string.Join(", ", await store.ListAliasesAsync(Ct)));
    }
}
