using System.Text;
using Encoding = System.Text.Encoding;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Transport;
using PubSubReceivedMessage = Google.Cloud.PubSub.V1.ReceivedMessage;
using ReceivedMessage = SourceFlow.Transport.ReceivedMessage;
using Status = Grpc.Core.Status;

namespace SourceFlow.Cloud.Tests.Gcp;

/// <summary>UT-GCP-01..25 that need no broker: options, wire mapping, naming, filters, batching, settlement, errors.</summary>
[Trait("Cloud", "Gcp")]
public sealed class GcpTransportTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // ================================================================ options (doc 07 §6, GAP-03)

    [Fact]
    [Trait("Id", "UT-GCP-01")]
    [Trait("Covers", "doc 07 6")]
    public void Options_Defaults_AreValid_OnceTheProjectIsSet_AndMatchThePubSubLimits()
    {
        var options = new GcpTransportOptions();
        Should.Throw<ArgumentException>(options.Validate).ParamName.ShouldBe("ProjectId");   // no silent default project

        options.ProjectId = "acme-prod";
        Should.NotThrow(options.Validate);
        options.EmulatorDetection.ShouldBe(EmulatorDetection.EmulatorOrProduction);
        options.Credential.ShouldBeNull();   // Application Default Credentials
        options.MaxRpcRetries.ShouldBe(3);
        options.MessageRetention.ShouldBe(TimeSpan.FromDays(7));
        options.NamePrefix.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Id", "UT-GCP-02")]
    [Trait("Covers", "doc 07 6;GAP-03")]
    [InlineData("project")]
    [InlineData("detection")]
    [InlineData("endpoint")]
    [InlineData("retries")]
    [InlineData("timeout")]
    [InlineData("poll")]
    [InlineData("retention")]
    [InlineData("prefix")]
    public void Options_ThatCannotBeHonoured_FailValidation(string option)
    {
        var options = new GcpTransportOptions { ProjectId = "acme-prod" };
        switch (option)
        {
            case "project": options.ProjectId = "Acme_Prod"; break;
            case "detection": options.EmulatorDetection = (EmulatorDetection)42; break;
            case "endpoint": options.Endpoint = " "; break;
            case "retries": options.MaxRpcRetries = 11; break;
            case "timeout": options.Timeout = TimeSpan.Zero; break;
            case "poll": options.PollInterval = TimeSpan.FromMinutes(1); break;
            case "retention": options.MessageRetention = TimeSpan.FromDays(8); break;
            case "prefix": options.NamePrefix = "prod~"; break;
        }

        var mocks = new GcpMocks();
        Should.Throw<ArgumentException>(options.Validate);
        Should.Throw<ArgumentException>(() => new GcpTransport(mocks.Publisher, mocks.Subscriber, options));
        Should.Throw<ArgumentException>(() => new ServiceCollection().AddSourceFlow(sf =>
        {
            sf.ServiceName = "probe";
            sf.UseGcpTransport(o => Copy(options, o));
        }));
    }

    [Fact]
    [Trait("Id", "UT-GCP-03")]
    [Trait("Covers", "doc 07 6")]
    public void Options_MapToTheClientBuilders_AndToEveryCallsDeadlineAndRetries()
    {
        GoogleCredential credential = GoogleCredential.FromAccessToken("token");
        var options = new GcpTransportOptions
        {
            ProjectId = "acme-prod",
            EmulatorDetection = EmulatorDetection.ProductionOnly,
            Endpoint = "europe-west1-pubsub.googleapis.com:443",
            Credential = credential,
            MaxRpcRetries = 5,
            Timeout = TimeSpan.FromSeconds(12),
        };

        PublisherServiceApiClientBuilder publisher = options.ToPublisherBuilder();
        SubscriberServiceApiClientBuilder subscriber = options.ToSubscriberBuilder();
        CallSettings call = options.ToCallSettings();

        publisher.Endpoint.ShouldBe("europe-west1-pubsub.googleapis.com:443");
        subscriber.Endpoint.ShouldBe("europe-west1-pubsub.googleapis.com:443");
        publisher.EmulatorDetection.ShouldBe(EmulatorDetection.ProductionOnly);
        subscriber.GoogleCredential.ShouldBeSameAs(credential);
        call.Expiration!.Timeout.ShouldBe(TimeSpan.FromSeconds(12));
        call.Retry!.MaxAttempts.ShouldBe(6);
        call.Retry.RetryFilter!(new RpcException(new Status(StatusCode.Unavailable, "down"))).ShouldBeTrue();
        call.Retry.RetryFilter(new RpcException(new Status(StatusCode.InvalidArgument, "bad"))).ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-GCP-04")]
    [Trait("Covers", "doc 07 6")]
    public void EmulatorOnly_WithoutPubSubEmulatorHost_FailsAtConstruction()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("PUBSUB_EMULATOR_HOST") is { Length: > 0 }, "PUBSUB_EMULATOR_HOST is set (CI); GcpEmulatorTests covers detection.");

        Should.Throw<InvalidOperationException>(() => new GcpTransport(new GcpTransportOptions { ProjectId = "acme-prod", EmulatorDetection = EmulatorDetection.EmulatorOnly }));
    }

    // ================================================================ wire (doc 07 §4, CloudEvents Pub/Sub binding)

    [Fact]
    [Trait("Id", "UT-GCP-05")]
    [Trait("Covers", "ADR-011;doc 07 4")]
    public void Encode_UsesCeAttributes_AndTheBodyByteForByte()
    {
        MessageEnvelope envelope = GcpMocks.Envelope("acct-1") with
        {
            ContentType = "application/octet-stream",
            Body = new byte[] { 0xFF, 0x00, 0xC3, 0x28 },
            Subject = "acct-1",
            TenantId = "eu",
            Headers = new Dictionary<string, string> { ["x-origin"] = "web" },
        };

        GcpOutgoingMessage wire = GcpWire.Encode(envelope);

        wire.Message.Attributes.Keys.ShouldAllBe(k => k.StartsWith("ce-", StringComparison.Ordinal));
        wire.Message.Attributes["ce-type"].ShouldBe("orders.placed.v1");
        wire.Message.Attributes["ce-id"].ShouldBe(envelope.MessageId.ToString());
        wire.Message.Data.ToByteArray().ShouldBe([0xFF, 0x00, 0xC3, 0x28]);
        GcpWire.Decode(wire.Message).ShouldBe(envelope);
        GcpWire.Encode(envelope, reason: "poison").Message.Attributes[GcpWire.ReasonAttribute].ShouldBe("poison");
        GcpWire.Encode(envelope with { Body = ReadOnlyMemory<byte>.Empty }).Message.Data.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    [Trait("Id", "UT-GCP-06")]
    [Trait("Covers", "ADR-011;doc 07 4")]
    public void LongAttributeValues_AreChunkedWithinThePubSubLimit_AndJoinedOnReceipt()
    {
        string subject = string.Concat(Enumerable.Repeat("ñ🔑", 700));   // 4,200 bytes, surrogate pairs on chunk edges
        var headers = Enumerable.Range(1, EnvelopeLimits.MaxHeaders).ToDictionary(i => $"x-header-{i}", i => new string('é', 120));
        MessageEnvelope envelope = GcpMocks.Envelope() with { Subject = subject, Headers = headers };

        PubsubMessage message = GcpWire.Encode(envelope).Message;

        message.Attributes.Values.ShouldAllBe(v => Encoding.UTF8.GetByteCount(v) <= GcpWire.MaxAttributeValueBytes);
        message.Attributes.Keys.ShouldContain("ce-subject#0");
        message.Attributes.Keys.ShouldContain("ce-subject#4");
        message.Attributes.ShouldNotContainKey("ce-subject");
        GcpWire.Decode(message).ShouldBe(envelope);
    }

    [Fact]
    [Trait("Id", "UT-GCP-07")]
    [Trait("Covers", "ADR-011;doc 07 4")]
    public void Decode_RejectsWhatIsNotACompleteSourceFlowCloudEvent_AndEncodeRefusesTooManyChunks()
    {
        var foreign = new PubsubMessage { Data = ByteString.CopyFromUtf8("{}") };
        foreign.Attributes["origin"] = "elsewhere";
        PubsubMessage chunked = GcpWire.Encode(GcpMocks.Envelope() with { Subject = new string('s', 3000) }).Message;
        chunked.Attributes.Remove("ce-subject#1");

        Should.Throw<FormatException>(() => GcpWire.Decode(foreign));
        Should.Throw<FormatException>(() => GcpWire.Decode(chunked)).Message.ShouldContain("missing chunks");
        Should.Throw<MessageTooLargeException>(() => GcpWire.Encode(GcpMocks.Envelope() with { Subject = new string('s', 100 * 1024) }));
    }

    [Fact]
    [Trait("Id", "UT-GCP-08")]
    [Trait("Covers", "GAP-13;doc 07 4")]
    public void OrderingKey_IsThePartitionKey_EmptyWithoutOne_AndHashedWhenTooLong()
    {
        GcpWire.OrderingKey(GcpMocks.Envelope("acct-1")).ShouldBe("acct-1");
        GcpWire.OrderingKey(GcpMocks.Envelope()).ShouldBeEmpty();
        string longKey = new('k', 2000);
        string hashed = GcpWire.OrderingKey(GcpMocks.Envelope(longKey));

        hashed.ShouldStartWith("k-");
        hashed.ShouldBe(GcpWire.OrderingKey(GcpMocks.Envelope(longKey)));   // deterministic: one key, one order
        GcpWire.Encode(GcpMocks.Envelope("acct-1")).Message.OrderingKey.ShouldBe("acct-1");
    }

    [Fact]
    [Trait("Id", "UT-GCP-09")]
    [Trait("Covers", "GAP-13;doc 07 4")]
    public async Task Commands_GoToTheEndpointTopic_EventsToTheirTopic_WithTheOrderingKeySet()
    {
        var mocks = new GcpMocks();
        MessageEnvelope command = GcpMocks.Envelope("acct-1");
        MessageEnvelope @event = GcpMocks.Envelope("acct-2");

        await mocks.Transport.SendAsync("ledger", [command], None);
        await mocks.Transport.PublishAsync("ledger-events", [@event], None);

        mocks.Published[0].TopicAsTopicName.ShouldBe(new TopicName(GcpMocks.Project, "ledger+in"));
        mocks.Published[0].Messages.Single().OrderingKey.ShouldBe("acct-1");   // publisher ordering, for ordered subscriptions
        mocks.Published[1].TopicAsTopicName.ShouldBe(new TopicName(GcpMocks.Project, "ledger-events"));
        mocks.Published[1].Messages.Single().OrderingKey.ShouldBe("acct-2");
    }

    // ================================================================ naming (doc 07 §3, GAP-13)

    [Fact]
    [Trait("Id", "UT-GCP-10")]
    [Trait("Covers", "GAP-13;doc 07 3")]
    public void Names_GiveEachSubscribingServiceItsOwnSubscription_AndNeverCollide()
    {
        GcpNaming.Topic("prod-", "payments.settled").ShouldBe("prod-payments.settled");
        GcpNaming.EndpointTopic("prod-", "billing").ShouldBe("prod-billing+in");
        GcpNaming.EndpointSubscription("prod-", "billing").ShouldBe("prod-billing");
        GcpNaming.DeadLetter("prod-", "billing").ShouldBe("prod-billing+dlq");

        // GAP-13: v2 shared one {topic}-sub subscription, so services competed for events.
        GcpNaming.TopicSubscription("prod-", "billing", "payments").ShouldBe("prod-billing~payments");
        GcpNaming.TopicSubscription("prod-", "shipping", "payments").ShouldNotBe(GcpNaming.TopicSubscription("prod-", "billing", "payments"));

        // An endpoint and a topic of the same name are different resources; logical '~' and '+' cannot forge suffixes.
        GcpNaming.EndpointTopic("", "billing").ShouldNotBe(GcpNaming.Topic("", "billing"));
        GcpNaming.Topic("", "billing+in").ShouldBe("billing-in");
        GcpNaming.TopicSubscription("", "a~b", "c").ShouldNotBe(GcpNaming.TopicSubscription("", "a", "b~c"));
    }

    [Fact]
    [Trait("Id", "UT-GCP-11")]
    [Trait("Covers", "doc 07 3")]
    public void Ids_StartWithALetter_AvoidTheGoogPrefix_AndFitThe255Limit()
    {
        GcpNaming.Topic("", "1orders").ShouldBe("sf-1orders");
        GcpNaming.Topic("", "google-events").ShouldBe("sf-google-events");
        GcpNaming.Topic("", "ab").ShouldBe("sf-ab");
        string logical = new('x', 300);

        string subscription = GcpNaming.TopicSubscription("prod-", logical, logical);
        string deadLetter = GcpNaming.DeadLetter("prod-", logical);

        subscription.Length.ShouldBe(255);
        subscription.ShouldBe(GcpNaming.TopicSubscription("prod-", logical, logical));
        deadLetter.Length.ShouldBe(255);
        deadLetter.ShouldEndWith("+dlq");
        GcpNaming.DeadLetter("prod-", logical + "y").ShouldNotBe(deadLetter);
    }

    // ================================================================ filters (doc 07 §7)

    [Fact]
    [Trait("Id", "UT-GCP-14")]
    [Trait("Covers", "doc 07 7;ADR-033")]
    public void TypeAndPrefixFilters_BecomePubSubFiltersOnCeType()
    {
        GcpFilter.Translate(CloudEventFilter.ForTypes(["payments.settled.v1", "payments.failed.v2"]))
            .ShouldBe("attributes.ce-type = \"payments.failed.v2\" OR attributes.ce-type = \"payments.settled.v1\"");
        GcpFilter.Translate("type LIKE 'payments.%'").ShouldBe("hasPrefix(attributes.ce-type, \"payments.\")");
        GcpFilter.Translate((string?)null).ShouldBeNull();
    }

    [Theory]
    [Trait("Id", "UT-GCP-15")]
    [Trait("Covers", "doc 07 7;ADR-033")]
    [InlineData("type LIKE 'orders.%' AND sftenant = 'eu'")]
    [InlineData("EXISTS sfactor")]
    [InlineData("LOWER(source) = 'orders'")]
    public void OtherFilters_HaveNoPubSubFilter_AndAreEvaluatedByTheReceiver(string filter)
    {
        GcpFilter.Translate(filter).ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "UT-GCP-15")]
    [Trait("Covers", "doc 07 7")]
    public void FiltersLongerThanThePubSubLimit_AreEvaluatedByTheReceiver()
    {
        string[] types = [.. Enumerable.Range(0, 10).Select(i => $"orders.some-rather-long-event-name-{i}.v1")];

        GcpFilter.Translate(CloudEventFilter.ForTypes(types)).ShouldBeNull();   // over 256 bytes
        GcpFilter.Translate(CloudEventFilter.ForTypes(types[..2])).ShouldNotBeNull();
    }

    [Fact]
    [Trait("Id", "UT-GCP-15")]
    [Trait("Covers", "doc 07 7;RX-R5")]
    public async Task TheReceiver_AppliesFiltersPubSubCannotExpress_PerTopicSubscription_NeverToCommands()
    {
        var mocks = new GcpMocks().WithEndpoint();
        mocks.AddSubscription("orders~orders-topic", "orders-topic", deadLetterTopic: "orders+dlq");
        mocks.AddSubscription("orders~billing-topic", "billing-topic", filter: "attributes.ce-type = \"billing.sent.v1\"", deadLetterTopic: "orders+dlq");
        MessageEnvelope command = GcpMocks.Envelope(type: "orders.cancel");
        MessageEnvelope euOrder = GcpMocks.Envelope(type: "orders.placed") with { TenantId = "eu" };
        MessageEnvelope usOrder = GcpMocks.Envelope(type: "orders.placed") with { TenantId = "us" };
        MessageEnvelope billing = GcpMocks.Envelope(type: "billing.sent");
        mocks.Deliver("orders", GcpMocks.Message(command));
        mocks.Deliver("orders~orders-topic", GcpMocks.Message(euOrder), GcpMocks.Message(usOrder));
        mocks.Deliver("orders~billing-topic", GcpMocks.Message(billing));
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", ["orders-topic", "billing-topic"])
        {
            Filters = new Dictionary<string, string> { ["orders-topic"] = "sftenant = 'eu'", ["billing-topic"] = "type = 'billing.sent.v1'" },
        }, None);

        IReadOnlyList<ReceivedMessage> received = await receiver.ReceiveAsync(10, TimeSpan.Zero, None);

        received.Select(r => r.Envelope.MessageId).Order().ShouldBe(new[] { command.MessageId, euOrder.MessageId, billing.MessageId }.Order());
        mocks.Acknowledged.ShouldBe(["ack-" + usOrder.MessageId]);   // acknowledged unseen; the billing filter is native
    }

    [Fact]
    [Trait("Id", "UT-GCP-16")]
    [Trait("Covers", "ADR-017;doc 07 5")]
    public void FilterComparison_IgnoresTermOrderAndWhitespace_ButNotContent()
    {
        GcpFilter.Equivalent("attributes.ce-type = \"a.v1\" OR attributes.ce-type = \"b.v1\"", "attributes.ce-type  =  \"b.v1\"  OR attributes.ce-type = \"a.v1\"").ShouldBeTrue();
        GcpFilter.Equivalent(null, "").ShouldBeTrue();
        GcpFilter.Equivalent("attributes.ce-type = \"a.v1\"", "attributes.ce-type = \"drifted.v1\"").ShouldBeFalse();
        GcpFilter.Equivalent(null, "attributes.ce-type = \"a.v1\"").ShouldBeFalse();
    }

    // ================================================================ publishing

    [Fact]
    [Trait("Id", "UT-GCP-17")]
    [Trait("Covers", "doc 07 4")]
    public async Task Sends_AreSplitIntoBatchesOfAHundred_InOrder()
    {
        var mocks = new GcpMocks();
        MessageEnvelope[] messages = [.. Enumerable.Range(0, 250).Select(i => GcpMocks.Envelope("k", $"{{\"n\":{i}}}"))];

        await mocks.Transport.SendAsync("orders", messages, None);

        mocks.Published.Select(p => p.Messages.Count).ShouldBe([100, 100, 50]);
        mocks.Published.SelectMany(p => p.Messages).Select(m => m.Attributes["ce-id"]).ShouldBe(messages.Select(m => m.MessageId.ToString()));
    }

    [Fact]
    [Trait("Id", "UT-GCP-18")]
    [Trait("Covers", "doc 07 4")]
    public async Task Batches_StayWithinThe10MbRequestLimit()
    {
        var mocks = new GcpMocks();
        MessageEnvelope[] messages = [.. Enumerable.Range(0, 5).Select(_ => GcpMocks.Envelope() with { Body = new byte[4 * 1024 * 1024] })];

        await mocks.Transport.SendAsync("orders", messages, None);

        mocks.Published.Select(p => p.Messages.Count).ShouldBe([2, 2, 1]);
        mocks.Published.ShouldAllBe(p => p.CalculateSize() <= GcpWire.MaxRequestBytes);
    }

    [Fact]
    [Trait("Id", "UT-GCP-19")]
    [Trait("Covers", "doc 07 4;NFR-PERF-07")]
    public async Task OversizedMessages_AreRefusedBeforeSending_AndAFailedCallFailsTheRest()
    {
        var mocks = new GcpMocks();
        MessageEnvelope first = GcpMocks.Envelope();
        MessageEnvelope tooLarge = GcpMocks.Envelope() with { Body = new byte[GcpWire.MaxBodyBytes + 1] };
        MessageEnvelope last = GcpMocks.Envelope();

        TransportSendException refused = await Should.ThrowAsync<TransportSendException>(() => mocks.Transport.SendAsync("orders", [first, tooLarge, last], None).AsTask());

        refused.Failures.Single().MessageId.ShouldBe(tooLarge.MessageId);
        refused.Failures[0].Error.ShouldBeOfType<MessageTooLargeException>();
        refused.Sent.ShouldBe(2);
        refused.IsTransient.ShouldBeFalse();

        int calls = 0;
        mocks.Publisher.PublishAsync(Arg.Any<PublishRequest>(), Arg.Any<CallSettings>()).Returns(_ => ++calls == 1
            ? Task.FromResult(new PublishResponse { MessageIds = { Enumerable.Range(0, 100).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)) } })
            : Task.FromException<PublishResponse>(new RpcException(new Status(StatusCode.Unavailable, "down"))));
        MessageEnvelope[] many = [.. Enumerable.Range(0, 150).Select(_ => GcpMocks.Envelope())];

        TransportSendException partial = await Should.ThrowAsync<TransportSendException>(() => mocks.Transport.SendAsync("orders", many, None).AsTask());

        partial.Sent.ShouldBe(100);
        partial.Failures.Select(f => f.MessageId).ShouldBe(many.Skip(100).Select(m => m.MessageId));
        partial.IsTransient.ShouldBeTrue();
    }

    [Fact]
    [Trait("Id", "UT-GCP-20")]
    [Trait("Covers", "ADR-033;doc 07 4")]
    public async Task GrpcErrors_MapToTypedTransportErrors()
    {
        var status = new Google.Rpc.Status { Code = (int)Code.ResourceExhausted, Message = "slow down" };
        status.Details.Add(Any.Pack(new RetryInfo { RetryDelay = Duration.FromTimeSpan(TimeSpan.FromSeconds(4)) }));
        var throttled = new RpcException(new Status(StatusCode.ResourceExhausted, "slow down"), new Metadata { { "grpc-status-details-bin", status.ToByteArray() } });

        GcpErrors.Map(new RpcException(new Status(StatusCode.NotFound, "gone")), "orders").ShouldBeOfType<DestinationNotFoundException>().Destination.ShouldBe("orders");
        GcpErrors.Map(throttled, "orders").ShouldBeOfType<TransportThrottledException>().RetryAfter.ShouldBe(TimeSpan.FromSeconds(4));
        GcpErrors.Map(new RpcException(new Status(StatusCode.ResourceExhausted, "quota")), "orders")!.RetryAfter.ShouldBe(TimeSpan.FromSeconds(1));
        GcpErrors.Map(new RpcException(new Status(StatusCode.Unavailable, "down")), "orders").ShouldBeOfType<TransportUnavailableException>().IsTransient.ShouldBeTrue();
        GcpErrors.Map(new RpcException(new Status(StatusCode.Unauthenticated, "token expired")), "orders")!.IsTransient.ShouldBeTrue();
        GcpErrors.Map(new RpcException(new Status(StatusCode.PermissionDenied, "no")), "orders")!.IsTransient.ShouldBeFalse();
        GcpErrors.Map(new HttpRequestException("refused"), "orders").ShouldBeOfType<TransportUnavailableException>();
        GcpErrors.Map(new ArgumentException("bug"), "orders").ShouldBeNull();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => GcpErrors.CallAsync<int>("orders", () => throw new RpcException(new Status(StatusCode.Cancelled, "cancelled")), cancelled.Token));
        await Should.ThrowAsync<DestinationNotFoundException>(() => new GcpMocks().Transport.CreateReceiverAsync(new ReceiverOptions("missing", []), None).AsTask());
    }

    // ================================================================ receiving and settlement

    [Fact]
    [Trait("Id", "UT-GCP-21")]
    [Trait("Covers", "RX-R5;doc 07 4")]
    public async Task Receive_PullsEachSubscriptionInTurn_ReturningImmediately_AndLocksForTheLockDuration()
    {
        var mocks = new GcpMocks().WithEndpoint();
        mocks.AddSubscription("orders~payments", "payments", deadLetterTopic: "orders+dlq");
        MessageEnvelope command = GcpMocks.Envelope();
        MessageEnvelope @event = GcpMocks.Envelope();
        mocks.Deliver("orders", GcpMocks.Message(command));
        mocks.Deliver("orders~payments", GcpMocks.Message(@event));
        await using ITransportReceiver locked = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", ["payments"]) { LockDuration = TimeSpan.FromSeconds(12) }, None);

        IReadOnlyList<ReceivedMessage> received = await locked.ReceiveAsync(10, TimeSpan.Zero, None);

        received.Count.ShouldBe(2);
        mocks.Pulls.Select(p => p.SubscriptionAsSubscriptionName.SubscriptionId).Order().ShouldBe(["orders", "orders~payments"]);
#pragma warning disable CS0612, CS0618
        mocks.Pulls.ShouldAllBe(p => p.ReturnImmediately);
#pragma warning restore CS0612, CS0618
        mocks.Deadlines.Select(d => d.Seconds).Distinct().ShouldBe([12]);   // the lock, not the subscription's 30 s

        // The next round starts with the other subscription, and the total stays within the maximum.
        mocks.Pulls.Clear();
        mocks.Deadlines.Clear();
        mocks.Deliver("orders", GcpMocks.Message(GcpMocks.Envelope()), GcpMocks.Message(GcpMocks.Envelope()));
        mocks.Deliver("orders~payments", GcpMocks.Message(GcpMocks.Envelope()));
        await using ITransportReceiver standard = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", ["payments"]), None);

        (await standard.ReceiveAsync(1, TimeSpan.Zero, None)).Count.ShouldBe(1);
        mocks.Pulls.Single().MaxMessages.ShouldBe(1);
        mocks.Deadlines.ShouldBeEmpty();   // a 30 s lock is the subscription's ack deadline already
    }

    [Fact]
    [Trait("Id", "UT-GCP-21")]
    [Trait("Covers", "doc 07 4")]
    public async Task Receive_WaitsByPollingUntilTheWaitIsOver()
    {
        var mocks = new GcpMocks().WithEndpoint();
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []), None);

        IReadOnlyList<ReceivedMessage> nothing = await receiver.ReceiveAsync(10, TimeSpan.FromMilliseconds(250), None);

        nothing.ShouldBeEmpty();
        mocks.Pulls.Count.ShouldBeGreaterThanOrEqualTo(2);   // every 10 ms (PollInterval) for 250 ms; a loaded runner still pulls twice
    }

    [Fact]
    [Trait("Id", "UT-GCP-22")]
    [Trait("Covers", "doc 04 9.2;doc 07 4")]
    public async Task Abandon_DelaysWithTheAckDeadline_CappedAtTenMinutes_AndKeepsOrderOnOrderedSubscriptions()
    {
        var mocks = new GcpMocks().WithEndpoint().WithEndpoint("ledger", ordered: true);
        MessageEnvelope[] messages = [GcpMocks.Envelope(), GcpMocks.Envelope(), GcpMocks.Envelope()];
        mocks.Deliver("orders", [.. messages.Select(m => GcpMocks.Message(m))]);
        MessageEnvelope ordered = GcpMocks.Envelope("acct");
        mocks.Deliver("ledger", GcpMocks.Message(ordered));
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []), None);
        await using ITransportReceiver ledger = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("ledger", []), None);
        IReadOnlyList<ReceivedMessage> received = await receiver.ReceiveAsync(10, TimeSpan.Zero, None);

        await received[0].AbandonAsync(TimeSpan.FromSeconds(2.5), None);
        await received[1].AbandonAsync(TimeSpan.Zero, None);
        await received[2].AbandonAsync(TimeSpan.FromHours(1), None);
        await (await ledger.ReceiveAsync(1, TimeSpan.Zero, None))[0].AbandonAsync(TimeSpan.Zero, None);

        mocks.Deadlines.ShouldBe([("orders", "ack-" + messages[0].MessageId, 3), ("orders", "ack-" + messages[1].MessageId, 0), ("orders", "ack-" + messages[2].MessageId, 600), ("ledger", "ack-" + ordered.MessageId, 1)]);
    }

    [Fact]
    [Trait("Id", "UT-GCP-23")]
    [Trait("Covers", "doc 07 4")]
    public async Task Renew_ExtendsTheAckDeadlineByTheLock_AndALapsedLockIsReported()
    {
        var mocks = new GcpMocks().WithEndpoint();
        MessageEnvelope envelope = GcpMocks.Envelope();
        mocks.Deliver("orders", GcpMocks.Message(envelope));
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []) { LockDuration = TimeSpan.FromSeconds(1) }, None);
        ReceivedMessage held = (await receiver.ReceiveAsync(1, TimeSpan.Zero, None)).Single();
        mocks.Deadlines.Clear();

        await held.RenewAsync(None);
        mocks.Deadlines.ShouldBe([("orders", "ack-" + envelope.MessageId, 1)]);

        await Task.Delay(TimeSpan.FromSeconds(1.2), TimeProvider.System, TestContext.Current.CancellationToken);   // Pub/Sub would accept the change silently
        TransportException lost = await Should.ThrowAsync<TransportException>(() => held.RenewAsync(None).AsTask());
        lost.IsTransient.ShouldBeFalse();
        lost.Message.ShouldContain("lock was lost");
    }

    [Fact]
    [Trait("Id", "UT-GCP-24")]
    [Trait("Covers", "GAP-11;doc 07 3;doc 07 4")]
    public async Task DeliveryLimit_AndMalformedMessages_GoToTheDeadLetterTopic_WithTheirReason()
    {
        var mocks = new GcpMocks().WithEndpoint(maxDeliveries: 2);
        MessageEnvelope fine = GcpMocks.Envelope();
        MessageEnvelope exhausted = GcpMocks.Envelope();
        var malformed = new PubSubReceivedMessage { AckId = "ack-malformed", DeliveryAttempt = 1, Message = new PubsubMessage { Data = ByteString.CopyFromUtf8("not a cloud event") } };
        mocks.Deliver("orders", GcpMocks.Message(fine, deliveryAttempt: 2), GcpMocks.Message(exhausted, deliveryAttempt: 3), malformed);
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []), None);

        IReadOnlyList<ReceivedMessage> received = await receiver.ReceiveAsync(10, TimeSpan.Zero, None);

        received.Single().Envelope.MessageId.ShouldBe(fine.MessageId);
        received[0].DeliveryCount.ShouldBe(2);
        mocks.Published.ShouldAllBe(p => p.TopicAsTopicName.TopicId == "orders+dlq");
        PubsubMessage[] moved = [.. mocks.Published.SelectMany(p => p.Messages)];
        moved[0].Attributes[GcpWire.ReasonAttribute].ShouldBe("MaxDeliveryAttemptsExceeded");
        GcpWire.Decode(moved[0]).ShouldBe(exhausted);
        moved[1].Attributes[GcpWire.ReasonAttribute].ShouldStartWith("MalformedMessage");
        moved[1].Data.ToStringUtf8().ShouldBe("not a cloud event");
        mocks.Acknowledged.ShouldBe(["ack-" + exhausted.MessageId, "ack-malformed"]);

        // An explicit dead letter carries its reason, which the dead-letter receiver reports.
        await received[0].DeadLetterAsync("poison", None);
        mocks.Published[^1].Messages.Single().Attributes[GcpWire.ReasonAttribute].ShouldBe("poison");
        mocks.AddSubscription("orders+dlq", "orders+dlq", maxDeliveries: null, deadLetterTopic: null);
        mocks.Deliver("orders+dlq", new PubSubReceivedMessage { AckId = "d1", Message = mocks.Published[^1].Messages[0] }, GcpMocks.Message(exhausted));
        await using ITransportReceiver deadLetters = await mocks.Transport.CreateDeadLetterReceiverAsync("orders", None);
        IReadOnlyList<ReceivedMessage> dead = await deadLetters.ReceiveAsync(10, TimeSpan.Zero, None);
        dead.Select(d => d.DeadLetterReason).ShouldBe(["poison", "MaxDeliveryAttemptsExceeded"]);
        await Should.ThrowAsync<TransportException>(() => dead[0].DeadLetterAsync("again", None).AsTask());
    }

    [Fact]
    [Trait("Id", "UT-GCP-24")]
    [Trait("Covers", "doc 07 4")]
    public async Task Dispose_ReleasesUnsettledMessagesAtOnce_NeverWhatWasCompleted()
    {
        var mocks = new GcpMocks().WithEndpoint();
        MessageEnvelope[] messages = [GcpMocks.Envelope(), GcpMocks.Envelope()];
        mocks.Deliver("orders", [.. messages.Select(m => GcpMocks.Message(m))]);
        ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []), None);
        IReadOnlyList<ReceivedMessage> received = await receiver.ReceiveAsync(10, TimeSpan.Zero, None);
        await received[0].CompleteAsync(None);

        await receiver.DisposeAsync();

        mocks.Acknowledged.ShouldBe(["ack-" + messages[0].MessageId]);
        mocks.Deadlines.ShouldBe([("orders", "ack-" + messages[1].MessageId, 0)]);
    }

    // ================================================================ health and registration

    [Fact]
    [Trait("Id", "UT-GCP-25")]
    [Trait("Covers", "doc 07 6")]
    public async Task HealthCheck_ReportsWhetherTheEndpointSubscriptionIsReachable()
    {
        var mocks = new GcpMocks().WithEndpoint();
        var down = new GcpMocks();
        down.Subscriber.GetSubscriptionAsync(Arg.Any<GetSubscriptionRequest>(), Arg.Any<CallSettings>()).Returns(Task.FromException<Subscription>(new RpcException(new Status(StatusCode.Unavailable, "down"))));

        (await new GcpTransportHealthCheck(mocks.Transport, "orders").CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Healthy);
        (await new GcpTransportHealthCheck(mocks.Transport, "billing").CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
        (await new GcpTransportHealthCheck(down.Transport, "orders").CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    [Trait("Id", "UT-GCP-25")]
    [Trait("Covers", "doc 07 6")]
    public void UseGcpTransport_RegistersTheTransport_AndTheHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "orders";
            sf.UseInMemoryStorage();
            sf.UseGcpTransport(o =>
            {
                o.ProjectId = "acme-prod";
                o.EmulatorDetection = EmulatorDetection.EmulatorOrProduction;   // the transport jobs set PUBSUB_EMULATOR_HOST
                o.Credential = GoogleCredential.FromAccessToken("token");
            });
        });
        services.AddHealthChecks().AddSourceFlowGcp();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITransport>().ShouldBeOfType<GcpTransport>().SystemName.ShouldBe("gcp_pubsub");
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value.Registrations.ShouldContain(r => r.Name == "sourceflow-gcp");
    }

    private static void Copy(GcpTransportOptions from, GcpTransportOptions to)
    {
        to.ProjectId = from.ProjectId;
        to.EmulatorDetection = from.EmulatorDetection;
        to.Endpoint = from.Endpoint;
        to.MaxRpcRetries = from.MaxRpcRetries;
        to.Timeout = from.Timeout;
        to.PollInterval = from.PollInterval;
        to.MessageRetention = from.MessageRetention;
        to.NamePrefix = from.NamePrefix;
    }
}
