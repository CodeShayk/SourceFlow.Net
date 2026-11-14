using System.Text.Json;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

// A string-keyed and an Int64-keyed id (WP-C.8, CR-16). Only these tests use them.
[StronglyTypedId(Backing = IdBacking.String, MaxLength = 32)]
public readonly partial record struct TagId(string Value);

[StronglyTypedId(Backing = IdBacking.Int64)]
public readonly partial record struct TicketNumber(long Value);

[MessageType("test.tag.tag", 1)]
public sealed record TagIt(TagId TagId) : ICommand<Tag>;

[MessageType("test.tag.tagged", 1)]
public sealed record Tagged(TagId TagId) : IEvent;

[Aggregate("test-tag")]
public sealed partial record Tag : IAggregate<Tag, TagId>
{
    public static Tag Initial { get; } = new();

    // An Int64 id in state, for UT-CAT-21: both catalogs describe it as the number the serializer writes.
    public TicketNumber Ticket { get; init; }

    public Decision Decide(TagIt c, DecisionContext ctx)
    {
        return Decision.Accept(new Tagged(c.TagId));
    }

    private Tag When(Tagged e)
    {
        return this;
    }
}

/// <summary>GEN-66..67 (WP-C.8, CR-16): an id that can't be a stream segment is refused, never stored (ADR-015).</summary>
[Trait("Category", "Unit")]
public sealed class IdValidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "GEN-66")]
    [Trait("Covers", "ADR-015")]
    public async Task AStringIdWithADash_IsRefused_AtSend_AndNeverWritten()
    {
        var dashed = new TagId("eu-42");

        ((IStronglyTypedId)dashed).IsValid.ShouldBeFalse();
        ((IStronglyTypedId)new TagId("eu42")).IsValid.ShouldBeTrue();
        TagId.TryParse("eu-42", out _).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => dashed.ToStreamSegment());
        Should.Throw<JsonException>(() => JsonSerializer.Serialize(dashed, Options()));
        Should.Throw<ArgumentException>(() => StreamId.For("test-tag", "eu-42"));

        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "tags";
            sf.AddModule<CoreTestModule>();
        }, cancellationToken: Ct);
        CommandResult result = await host.SendAsync(new TagIt(dashed), cancellationToken: Ct);

        result.Status.ShouldBe(CommandStatus.Rejected);
        result.Error.ShouldNotBeNull().Code.ShouldBe("sourceflow.invalid_command");
        LogBatch log = await host.Store.ReadAsync(GlobalPosition.Start, BucketRange.All, 1_000, Ct);
        log.Events.ShouldNotContain(e => e.StreamId.Value.StartsWith("test-tag-", StringComparison.Ordinal), "nothing was stored");
    }

    [Fact]
    [Trait("Id", "GEN-67")]
    [Trait("Covers", "ADR-015")]
    public void NegativeAndZeroInt64Ids_AreRefused()
    {
        ((IStronglyTypedId)new TicketNumber(-7)).IsValid.ShouldBeFalse();
        ((IStronglyTypedId)new TicketNumber(0)).IsValid.ShouldBeFalse();
        ((IStronglyTypedId)new TicketNumber(7)).IsValid.ShouldBeTrue();
        TicketNumber.TryParse("-7", out _).ShouldBeFalse();
        TicketNumber.TryParse("0", out _).ShouldBeFalse();
        TicketNumber.TryParse("7", out TicketNumber seven).ShouldBeTrue();
        seven.ToStreamSegment().ShouldBe("7");
        Should.Throw<InvalidOperationException>(() => new TicketNumber(-7).ToStreamSegment());
        Should.Throw<JsonException>(() => JsonSerializer.Serialize(new TicketNumber(-7), Options()));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<TicketNumber>("-7", Options()));
        JsonSerializer.Deserialize<TicketNumber>("7", Options()).ShouldBe(seven);
    }

    [Fact]
    [Trait("Id", "UT-CAT-21")]
    [Trait("Covers", "ADR-029 2")]
    public void Int64Ids_AreIntegers_InTheSchema_AndInSfql()
    {
        RuntimeModel model = new SourceFlowModelBuilder().AddModule<CoreTestModule>().Build();

        System.Text.Json.Nodes.JsonObject schema = SourceFlow.Catalog.JsonSchemaGenerator.Generate(model.JsonTypeInfoFor(typeof(Tag)), model.Registry.Options);
        schema["properties"]!["ticket"]!["type"]!.GetValue<string>().ShouldBe("integer");
        schema["properties"]!["ticket"]!["format"]!.GetValue<string>().ShouldBe("int64");
        TicketNumber.Backing.ShouldBe(IdBacking.Int64);
        TagId.Backing.ShouldBe(IdBacking.String);

        SourceFlow.Query.QueryCatalog catalog = SourceFlow.Query.QueryCatalog.Build(model);
        catalog.Aggregates.Single(a => a.Name == "test-tag").Fields.Single(f => f.Name == "ticket").Type.ShouldBe(SourceFlow.Query.QueryType.Integer);
    }

    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new TagId.SourceFlowJsonConverter());
        options.Converters.Add(new TicketNumber.SourceFlowJsonConverter());
        return options;
    }
}
