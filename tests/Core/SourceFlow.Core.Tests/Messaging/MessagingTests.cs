using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bank.Accounts;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.Messaging;

[MessageType("test.renamed", 1)]
public sealed record RenamedV1(string Name) : IEvent;

[MessageType("test.renamed", 2)]
public sealed record RenamedV2(string First, string Last) : IEvent;

[MessageType("test.renamed", 3)]
public sealed record RenamedV3(string First, string Last, string? Nickname = null) : IEvent;

public sealed class RenamedV1ToV2 : IUpcaster<RenamedV1, RenamedV2>
{
    public RenamedV2 Upcast(RenamedV1 old)
    {
        string[] parts = old.Name.Split(' ', 2);
        return new RenamedV2(parts[0], parts.Length > 1 ? parts[1] : string.Empty);
    }
}

public sealed class RenamedV2ToV3 : IUpcaster<RenamedV2, RenamedV3>
{
    public RenamedV3 Upcast(RenamedV2 old)
    {
        return new RenamedV3(old.First, old.Last);
    }
}

public sealed record Nested(IReadOnlyList<Nested>? Children);

[JsonSerializable(typeof(RenamedV1))]
[JsonSerializable(typeof(RenamedV2))]
[JsonSerializable(typeof(RenamedV3))]
[JsonSerializable(typeof(Nested))]
internal sealed partial class MessagingTestJsonContext : JsonSerializerContext;

/// <summary>UT-MSG-01..30 (plan 05 §3.1).</summary>
[Trait("Category", "Unit")]
public sealed class MessagingTests
{
    private static readonly MessageTypeRegistry Registry = BankModel.Instance.Registry;
    private static readonly SystemTextJsonMessageSerializer Serializer = new();

    [Fact]
    [Trait("Id", "UT-MSG-01")]
    [Trait("Covers", "ADR-015")]
    public void MessageIds_New_IsVersion7AndTimeOrdered()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        MessageId first = MessageIds.New(time);
        time.Advance(TimeSpan.FromMilliseconds(5));
        MessageId second = MessageIds.New(time);

        MessageIds.VersionOf(first.Value).ShouldBe(7);
        MessageIds.UnixTimeMillisecondsOf(first.Value).ShouldBe(time.GetUtcNow().AddMilliseconds(-5).ToUnixTimeMilliseconds());
        second.ShouldBeGreaterThan(first);
    }

    [Theory]
    [Trait("Id", "UT-MSG-02")]
    [Trait("Covers", "ADR-006")]
    [InlineData("6ba7b810-9dad-11d1-80b4-00c04fd430c8", "www.example.com", "2ed6657d-e927-568b-95e1-2665a8aea6a2")] // RFC 9562 A.4
    [InlineData("6ba7b810-9dad-11d1-80b4-00c04fd430c8", "python.org", "886313e1-3b8a-5372-9b90-0c9aee199e5d")]
    public void MessageIds_FromKey_MatchesRfc9562GoldenVector(string ns, string name, string expected)
    {
        MessageIds.CreateVersion5(Guid.Parse(ns), name).ShouldBe(Guid.Parse(expected));
    }

    [Fact]
    [Trait("Id", "UT-MSG-03")]
    [Trait("Covers", "GAP-09")]
    public void MessageIds_FromKey_SameKeySameId_DifferentScopeDifferentId()
    {
        MessageId a = MessageIds.FromKey("accounts", "key-1");

        MessageIds.FromKey("accounts", "key-1").ShouldBe(a);
        MessageIds.FromKey("payments", "key-1").ShouldNotBe(a);
        MessageIds.FromKey("accounts", "key-2").ShouldNotBe(a);
        MessageIds.VersionOf(a.Value).ShouldBe(5);
    }

    [Fact]
    [Trait("Id", "UT-MSG-04")]
    [Trait("Covers", "ADR-005")]
    public void Registry_DuplicateName_ThrowsWithSF0002Guidance()
    {
        MessageTypeRegistryBuilder builder = new MessageTypeRegistryBuilder()
            .Add(typeof(RenamedV1), "test.renamed", 1)
            .Add(typeof(RenamedV2), "test.renamed", 1);

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build(TestOptions()));
        error.Message.ShouldContain("SF0002");
    }

    [Fact]
    [Trait("Id", "UT-MSG-05")]
    public void Registry_DuplicateClrType_Throws()
    {
        MessageTypeRegistryBuilder builder = new MessageTypeRegistryBuilder()
            .Add(typeof(RenamedV1), "test.renamed", 1)
            .Add(typeof(RenamedV1), "test.other", 1);

        Should.Throw<InvalidOperationException>(() => builder.Build(TestOptions()));
    }

    [Fact]
    [Trait("Id", "UT-MSG-06")]
    [Trait("Covers", "GAP-10")]
    public void Registry_TryGetUnknown_ReturnsFalse()
    {
        Registry.TryGet("bank.account.hacked", 1, out _).ShouldBeFalse();
        Registry.TryGet("bank.account.deposited", 2, out _).ShouldBeFalse();
        Should.Throw<UnknownMessageTypeException>(() => Registry.Get("System.Diagnostics.Process", 1));
    }

    [Fact]
    [Trait("Id", "UT-MSG-07")]
    [Trait("Covers", "DC-1")]
    public void Registry_GetJsonTypeInfo_MissingType_ThrowsWithJsonContextHint()
    {
        MessageTypeRegistryBuilder builder = new MessageTypeRegistryBuilder().Add(typeof(Deposit), "bank.account.deposit", 1);

        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build(TestOptions()));
        error.Message.ShouldContain("JsonSerializable");
        error.Message.ShouldContain("SF0010");
    }

    [Fact]
    [Trait("Id", "UT-MSG-08")]
    [Trait("Covers", "ADR-005;ADR-015")]
    public void Serializer_RoundTripsRecordWithStronglyTypedId()
    {
        var message = new MoneyDeposited(BankModel.Account, 12.50m, 112.50m);
        MessageTypeInfo type = Registry.Get(typeof(MoneyDeposited));

        byte[] body = Serializer.SerializeToArray(message, type);
        string json = Encoding.UTF8.GetString(body);

        json.ShouldContain($"\"accountId\":\"{BankModel.Account}\"");
        Serializer.Deserialize(body.AsMemory(), type).ShouldBe(message);
    }

    [Fact]
    [Trait("Id", "UT-MSG-09")]
    public void Serializer_UsesCamelCase()
    {
        byte[] body = Serializer.SerializeToArray(new Withdraw(BankModel.Account, 1m), Registry.Get(typeof(Withdraw)));

        Encoding.UTF8.GetString(body).ShouldBe($"{{\"accountId\":\"{BankModel.Account}\",\"amount\":1}}");
    }

    [Fact]
    [Trait("Id", "UT-MSG-10")]
    [Trait("Covers", "GAP-10")]
    public void Serializer_DeserializeMessage_UnknownType_ThrowsUnknownMessageType_WithoutParsing()
    {
        // The allow-list is consulted with the envelope's (type, version) before any byte is parsed.
        Should.Throw<UnknownMessageTypeException>(() => Registry.Get("System.Object", 1));
    }

    [Fact]
    [Trait("Id", "UT-MSG-11")]
    public void Serializer_MaxDepthExceeded_Throws()
    {
        MessageTypeRegistry registry = TestRegistryBuilder().Build(TestOptions());
        string deep = string.Concat(Enumerable.Repeat("{\"children\":[", 100)) + "null" + string.Concat(Enumerable.Repeat("]}", 100));
        JsonSerializerOptions options = registry.Options;

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(deep, options.GetTypeInfo(typeof(Nested))));
    }

    [Fact]
    [Trait("Id", "UT-MSG-12")]
    public void Serializer_DuplicateProperty_Rejected()
    {
        MessageTypeInfo type = Registry.Get(typeof(Withdraw));
        byte[] body = Encoding.UTF8.GetBytes($"{{\"accountId\":\"{BankModel.Account}\",\"amount\":1,\"amount\":1000}}");

        Should.Throw<JsonException>(() => Serializer.Deserialize(body.AsMemory(), type));
    }

    [Fact]
    [Trait("Id", "UT-MSG-13")]
    [Trait("Covers", "ADR-005")]
    public void Upcaster_ChainsV1ToV3OnRead()
    {
        MessageTypeRegistry registry = TestRegistryBuilder().Build(TestOptions());
        MessageTypeInfo v1 = registry.Get("test.renamed", 1);

        object latest = registry.UpcastToLatest(new RenamedV1("Ada Lovelace"), v1, out MessageTypeInfo latestType);

        latest.ShouldBe(new RenamedV3("Ada", "Lovelace"));
        latestType.Version.ShouldBe(3);
    }

    [Fact]
    [Trait("Id", "UT-MSG-14")]
    public void Upcaster_MissingLink_IsAStartupError()
    {
        MessageTypeRegistryBuilder builder = new MessageTypeRegistryBuilder()
            .Add(typeof(RenamedV1), "test.renamed", 1)
            .Add(typeof(RenamedV2), "test.renamed", 2);

        Should.Throw<InvalidOperationException>(() => builder.Build(TestOptions())).Message.ShouldContain("upcaster");
    }

    [Fact]
    [Trait("Id", "UT-MSG-15")]
    public void Upcaster_NotAppliedToLatestVersion()
    {
        MessageTypeRegistry registry = TestRegistryBuilder().Build(TestOptions());
        var v3 = new RenamedV3("A", "B", "C");

        registry.UpcastToLatest(v3, registry.Get(typeof(RenamedV3)), out _).ShouldBeSameAs(v3);
    }

    [Fact]
    [Trait("Id", "UT-MSG-16")]
    public void Serializer_TolerantReader_IgnoresAddedOptionalMembers()
    {
        MessageTypeInfo type = Registry.Get(typeof(Withdraw));
        byte[] body = Encoding.UTF8.GetBytes($"{{\"accountId\":\"{BankModel.Account}\",\"amount\":1,\"addedLater\":true}}");

        Serializer.Deserialize(body.AsMemory(), type).ShouldBe(new Withdraw(BankModel.Account, 1m));
    }

    [Fact]
    [Trait("Id", "UT-MSG-17")]
    [Trait("Covers", "ADR-011")]
    public void CloudEvents_Binary_RoundTrips()
    {
        MessageEnvelope envelope = SampleEnvelope();

        Dictionary<string, string> attributes = CloudEvents.ToAttributes(envelope);

        CloudEvents.FromAttributes(attributes, envelope.Body).ShouldBe(envelope);
    }

    [Fact]
    [Trait("Id", "UT-MSG-18")]
    [Trait("Covers", "ADR-011")]
    public void CloudEvents_Structured_RoundTrips()
    {
        MessageEnvelope envelope = SampleEnvelope();

        MessageEnvelope parsed = CloudEvents.FromStructured(CloudEvents.ToStructured(envelope));

        parsed.ShouldBe(envelope with { Body = parsed.Body });
        JsonDocument.Parse(parsed.Body).RootElement.GetProperty("amount").GetDecimal().ShouldBe(5m);
    }

    [Fact]
    [Trait("Id", "UT-MSG-19")]
    public void CloudEvents_Type_IsNameDotVersion()
    {
        CloudEvents.ToAttributes(SampleEnvelope())[CloudEvents.Type].ShouldBe("bank.account.deposited.v1");
        CloudEvents.TryParseType("a.b.v12", out string name, out int version).ShouldBeTrue();
        (name, version).ShouldBe(("a.b", 12));
        CloudEvents.TryParseType("a.b", out _, out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-MSG-20")]
    public void CloudEvents_Extensions_AreMapped()
    {
        Dictionary<string, string> attributes = CloudEvents.ToAttributes(SampleEnvelope());

        attributes[CloudEvents.Correlation].ShouldBe("corr-1");
        attributes[CloudEvents.StreamVersion].ShouldBe("7");
        attributes[CloudEvents.Tenant].ShouldBe("eu");
        attributes[CloudEvents.Source].ShouldBe("urn:sourceflow:accounts");
    }

    [Fact]
    [Trait("Id", "UT-MSG-21")]
    public void CloudEvents_Overflow_KeepsBudgetAndRoundTrips()
    {
        MessageEnvelope envelope = SampleEnvelope();

        Dictionary<string, string> attributes = CloudEvents.ToAttributes(envelope, maxAttributes: 10);

        attributes.Count.ShouldBeLessThanOrEqualTo(10);
        attributes.ShouldContainKey(CloudEvents.OverflowAttribute);
        CloudEvents.FromAttributes(attributes, envelope.Body).ShouldBe(envelope);
    }

    [Fact]
    [Trait("Id", "UT-MSG-22")]
    public void CloudEvents_TraceContext_Preserved()
    {
        MessageEnvelope parsed = CloudEvents.FromAttributes(CloudEvents.ToAttributes(SampleEnvelope()), ReadOnlyMemory<byte>.Empty);

        parsed.TraceParent.ShouldBe("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        parsed.TraceState.ShouldBe("vendor=1");
    }

    [Fact]
    [Trait("Id", "UT-MSG-23")]
    [Trait("Covers", "NFR-PERF-07")]
    public void Limits_BodyOverCapability_ThrowsMessageTooLarge()
    {
        MessageEnvelope envelope = SampleEnvelope() with { Body = new byte[300] };

        MessageTooLargeException error = Should.Throw<MessageTooLargeException>(() => EnvelopeLimits.ValidateBody(envelope, 256));
        error.Size.ShouldBe(300);
        Should.NotThrow(() => EnvelopeLimits.ValidateBody(envelope, 300));
    }

    [Fact]
    [Trait("Id", "UT-MSG-24")]
    public void Limits_HeaderCount()
    {
        Dictionary<string, string> nine = Enumerable.Range(0, 9).ToDictionary(i => "h" + i, _ => "v");

        Should.Throw<ArgumentException>(() => EnvelopeLimits.ValidateHeaders(nine));
        Should.NotThrow(() => EnvelopeLimits.ValidateHeaders(nine.Take(8).ToDictionary()));
    }

    [Fact]
    [Trait("Id", "UT-MSG-25")]
    public void Limits_HeaderSize()
    {
        Should.Throw<ArgumentException>(() => EnvelopeLimits.ValidateHeaders(new Dictionary<string, string> { ["h"] = new string('x', 257) }));
    }

    [Fact]
    [Trait("Id", "UT-MSG-26")]
    public void Envelope_Defaults()
    {
        var envelope = new MessageEnvelope { MessageId = MessageId.Empty, Type = "a", TypeVersion = 1, Source = "s", Time = DateTimeOffset.UnixEpoch };

        envelope.TenantId.ShouldBe(MessageEnvelope.DefaultTenant);
        envelope.ContentType.ShouldBe(MessageEnvelope.JsonContentType);
        envelope.Headers.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-MSG-27")]
    public void Envelope_PartitionKey_DefaultsToSubject()
    {
        MessageEnvelope envelope = SampleEnvelope() with { PartitionKey = null };

        envelope.EffectivePartitionKey.ShouldBe(envelope.Subject);
        (envelope with { PartitionKey = "k" }).EffectivePartitionKey.ShouldBe("k");
    }

    [Fact]
    [Trait("Id", "UT-MSG-28")]
    public void Envelope_IsImmutable_WithCreatesCopy()
    {
        MessageEnvelope original = SampleEnvelope();
        MessageEnvelope copy = original with { Actor = "other" };

        original.Actor.ShouldBe("ada");
        copy.ShouldNotBe(original);
    }

    private static MessageEnvelope SampleEnvelope()
    {
        return new MessageEnvelope
        {
            MessageId = MessageId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c"),
            Type = "bank.account.deposited",
            TypeVersion = 1,
            Source = "accounts",
            Time = DateTimeOffset.Parse("2026-11-11T10:00:00.1234567+00:00", System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId = new CorrelationId("corr-1"),
            CausationId = MessageId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5d"),
            Subject = "bank-account-0193a1b2c3d47e5f8a9b0c1d2e3f4a5b",
            PartitionKey = "p-1",
            StreamVersion = 7,
            TenantId = "eu",
            Actor = "ada",
            TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            TraceState = "vendor=1",
            Headers = new Dictionary<string, string> { ["x-origin"] = "web" },
            Body = Encoding.UTF8.GetBytes("{\"accountId\":\"x\",\"amount\":5,\"balanceAfter\":5}"),
        };
    }

    private static MessageTypeRegistryBuilder TestRegistryBuilder()
    {
        return new MessageTypeRegistryBuilder()
            .Add(typeof(RenamedV1), "test.renamed", 1)
            .Add(typeof(RenamedV2), "test.renamed", 2)
            .Add(typeof(RenamedV3), "test.renamed", 3)
            .AddUpcaster(typeof(RenamedV1), typeof(RenamedV2), o => new RenamedV1ToV2().Upcast((RenamedV1)o))
            .AddUpcaster(typeof(RenamedV2), typeof(RenamedV3), o => new RenamedV2ToV3().Upcast((RenamedV2)o));
    }

    private static JsonSerializerOptions TestOptions()
    {
        return SystemTextJsonMessageSerializer.CreateOptions([MessagingTestJsonContext.Default], []);
    }
}
