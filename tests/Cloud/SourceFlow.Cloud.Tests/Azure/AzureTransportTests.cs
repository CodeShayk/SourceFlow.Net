using System.Text;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SourceFlow.Cloud.Azure;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Transport;
using ReceivedMessage = SourceFlow.Transport.ReceivedMessage;

namespace SourceFlow.Cloud.Tests.Azure;

/// <summary>UT-ASB-01..25 that need no broker: options, wire mapping, naming, rules, batching, settlement, errors.</summary>
[Trait("Cloud", "Azure")]
public sealed class AzureTransportTests
{
    private const string EmulatorConnection = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
    private static readonly CancellationToken None = CancellationToken.None;

    // ================================================================ options (doc 07 §6, GAP-03)

    [Fact]
    [Trait("Id", "UT-ASB-01")]
    [Trait("Covers", "doc 07 6")]
    public void Options_NeedExactlyOneConnection_AndDefaultToManagedIdentity()
    {
        Should.Throw<ArgumentException>(new AzureTransportOptions().Validate);   // neither
        Should.Throw<ArgumentException>(new AzureTransportOptions { FullyQualifiedNamespace = "acme.servicebus.windows.net", ConnectionString = EmulatorConnection }.Validate);   // both
        var options = new AzureTransportOptions { FullyQualifiedNamespace = "acme.servicebus.windows.net" };

        Should.NotThrow(options.Validate);
        options.Credential.ShouldBeNull();   // DefaultAzureCredential: managed identity in Azure
        options.MaxRetries.ShouldBe(3);
        options.LockDuration.ShouldBe(TimeSpan.FromSeconds(30));
        options.DuplicateDetectionWindow.ShouldBeNull();
        options.MaxConcurrentSessions.ShouldBe(8);
        Should.NotThrow(new AzureTransportOptions { ConnectionString = EmulatorConnection }.Validate);
    }

    [Theory]
    [Trait("Id", "UT-ASB-02")]
    [Trait("Covers", "doc 07 6;GAP-03")]
    [InlineData("uri")]
    [InlineData("connection")]
    [InlineData("admin")]
    [InlineData("retries")]
    [InlineData("timeout")]
    [InlineData("lock")]
    [InlineData("ttl")]
    [InlineData("dedup")]
    [InlineData("sessions")]
    [InlineData("prefix")]
    public void Options_ThatCannotBeHonoured_FailValidation(string option)
    {
        var options = new AzureTransportOptions { FullyQualifiedNamespace = "acme.servicebus.windows.net" };
        switch (option)
        {
            case "uri": options.FullyQualifiedNamespace = "sb://acme.servicebus.windows.net/"; break;
            case "connection": options.FullyQualifiedNamespace = null; options.ConnectionString = "not a connection string"; break;
            case "admin": options.AdministrationConnectionString = EmulatorConnection; break;
            case "retries": options.MaxRetries = 11; break;
            case "timeout": options.TryTimeout = TimeSpan.Zero; break;
            case "lock": options.LockDuration = TimeSpan.FromSeconds(3); break;
            case "ttl": options.MessageTimeToLive = TimeSpan.Zero; break;
            case "dedup": options.DuplicateDetectionWindow = TimeSpan.FromDays(8); break;
            case "sessions": options.MaxConcurrentSessions = 0; break;
            case "prefix": options.NamePrefix = "prod/"; break;
        }

        Should.Throw<Exception>(options.Validate).ShouldBeAssignableTo<Exception>().ShouldSatisfyAllConditions(e => (e is ArgumentException or FormatException).ShouldBeTrue(e.GetType().Name));
        Should.Throw<Exception>(() => new ServiceCollection().AddSourceFlow(sf =>
        {
            sf.ServiceName = "probe";
            sf.UseAzureTransport(o => Copy(options, o));
        }));
    }

    [Fact]
    [Trait("Id", "UT-ASB-03")]
    [Trait("Covers", "doc 07 6")]
    public async Task Options_MapToTheClientOptions_AndTheEntitySettings()
    {
        var options = new AzureTransportOptions
        {
            FullyQualifiedNamespace = "acme.servicebus.windows.net",
            TransportType = ServiceBusTransportType.AmqpWebSockets,
            MaxRetries = 5,
            TryTimeout = TimeSpan.FromSeconds(20),
            DuplicateDetectionWindow = TimeSpan.FromMinutes(10),
        };

        ServiceBusClientOptions client = options.ToClientOptions();

        client.TransportType.ShouldBe(ServiceBusTransportType.AmqpWebSockets);
        client.RetryOptions.MaxRetries.ShouldBe(5);
        client.RetryOptions.TryTimeout.ShouldBe(TimeSpan.FromSeconds(20));
        await using var transport = new AzureTransport(options);
        transport.Client.FullyQualifiedNamespace.ShouldBe("acme.servicebus.windows.net");
        transport.Capabilities.Deduplication.ShouldBeTrue();
        transport.Capabilities.DeduplicationWindow.ShouldBe(TimeSpan.FromMinutes(10));
        new AzureMocks().Transport.Capabilities.Deduplication.ShouldBeFalse();   // off by default: a replay keeps its message id
    }

    [Theory]
    [Trait("Id", "UT-ASB-04")]
    [Trait("Covers", "doc 07 6")]
    [InlineData("Development", false)]
    [InlineData("Production", true)]
    public async Task AConnectionString_OutsideDevelopment_IsLoggedAsAWarning(string environment, bool warned)
    {
        var logger = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        IHostEnvironment host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(environment);
        services.AddSingleton(host);
        services.AddSingleton<ILoggerFactory>(logger);
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "orders";
            sf.UseInMemoryStorage();
            sf.UseAzureTransport(o => o.ConnectionString = EmulatorConnection);
        });
        await using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITransport>().ShouldBeOfType<AzureTransport>();

        logger.Messages.Any(m => m.Contains("connection string", StringComparison.Ordinal) && m.Contains(environment, StringComparison.Ordinal)).ShouldBe(warned);
    }

    // ================================================================ wire (doc 07 §4, CloudEvents AMQP binding)

    [Fact]
    [Trait("Id", "UT-ASB-05")]
    [Trait("Covers", "ADR-011;doc 07 4")]
    public void Encode_UsesCloudEventsApplicationProperties_AndTheBodyByteForByte()
    {
        MessageEnvelope envelope = AzureMocks.Envelope("acct-1") with
        {
            ContentType = "application/octet-stream",
            Body = new byte[] { 0xFF, 0x00, 0xC3 },
            TenantId = "eu",
            Headers = new Dictionary<string, string> { ["x-origin"] = "web" },
        };

        ServiceBusMessage message = AzureWire.Encode(envelope, topic: "orders-events");

        message.MessageId.ShouldBe(envelope.MessageId.ToString());   // broker duplicate detection, when on
        message.SessionId.ShouldBe("acct-1");
        message.Subject.ShouldBe("orders.placed.v1");
        message.ContentType.ShouldBe("application/octet-stream");
        message.ApplicationProperties["cloudEvents:type"].ShouldBe("orders.placed.v1");
        message.ApplicationProperties["cloudEvents:sftenant"].ShouldBe("eu");
        message.ApplicationProperties[AzureWire.TopicProperty].ShouldBe("orders-events");
        message.Body.ToArray().ShouldBe([0xFF, 0x00, 0xC3]);
        AzureWire.Decode(message.ApplicationProperties.AsReadOnly(), message.Body).ShouldBe(envelope);
    }

    [Fact]
    [Trait("Id", "UT-ASB-06")]
    [Trait("Covers", "doc 07 4")]
    public void SessionId_IsThePartitionKey_OrTheIdWithoutOne_AndHashedWhenTooLong()
    {
        MessageEnvelope withoutKey = AzureMocks.Envelope();
        string longKey = new('k', 200);

        AzureWire.SessionId(AzureMocks.Envelope("acct-1")).ShouldBe("acct-1");
        AzureWire.SessionId(withoutKey).ShouldBe(withoutKey.MessageId.ToString());
        AzureWire.SessionId(AzureMocks.Envelope(longKey)).ShouldStartWith("k-");
        AzureWire.SessionId(AzureMocks.Envelope(longKey)).Length.ShouldBeLessThanOrEqualTo(AzureWire.MaxIdLength);
        AzureWire.SessionId(AzureMocks.Envelope(longKey)).ShouldBe(AzureWire.SessionId(AzureMocks.Envelope(longKey)));
    }

    [Fact]
    [Trait("Id", "UT-ASB-07")]
    [Trait("Covers", "ADR-011;doc 07 4")]
    public void Decode_RejectsAMessageThatIsNotASourceFlowCloudEvent()
    {
        var properties = new Dictionary<string, object> { ["origin"] = "elsewhere", ["cloudEvents:type"] = "no-version" };

        Should.Throw<FormatException>(() => AzureWire.Decode(properties, BinaryData.FromString("{}")));
    }

    [Fact]
    [Trait("Id", "UT-ASB-08")]
    [Trait("Covers", "doc 04 9.2;doc 07 4")]
    public void ADelayedRetryCopy_CarriesTheDeliveries_UnderABrokerIdOfItsOwn()
    {
        MessageEnvelope envelope = AzureMocks.Envelope("acct-1");
        ServiceBusReceivedMessage received = AzureMocks.Message(envelope, deliveryCount: 2, previous: 1);

        ServiceBusMessage copy = AzureWire.Retry(received, deliveries: 3);

        copy.MessageId.ShouldBe(envelope.MessageId + "/r3");   // not dropped by duplicate detection
        copy.SessionId.ShouldBe("acct-1");
        copy.ApplicationProperties[AzureWire.DeliveriesProperty].ShouldBe(3);
        AzureWire.Decode(copy.ApplicationProperties.AsReadOnly(), copy.Body).ShouldBe(envelope);
        AzureWire.PreviousDeliveries(received.ApplicationProperties).ShouldBe(1);
        AzureWire.PreviousDeliveries(AzureMocks.Message(envelope).ApplicationProperties).ShouldBe(0);
    }

    // ================================================================ naming and rules (doc 07 §3, §4)

    [Fact]
    [Trait("Id", "UT-ASB-09")]
    [Trait("Covers", "doc 07 3")]
    public void Names_ArePrefixedAndSanitised_AndSubscriptionsFitFiftyCharacters()
    {
        AzureNaming.Queue("prod-", "billing").ShouldBe("prod-billing");
        AzureNaming.Topic("prod-", "payments.settled").ShouldBe("prod-payments.settled");
        AzureNaming.Subscription("prod-", "billing").ShouldBe("prod-billing");
        AzureNaming.Queue("", "a b/c").ShouldBe("a-b-c");
        AzureNaming.Queue("", "-orders-").ShouldBe("sf-orders-0");
        string longEndpoint = new('x', 80);

        string subscription = AzureNaming.Subscription("prod-", longEndpoint);

        subscription.Length.ShouldBe(AzureNaming.MaxSubscriptionName);
        subscription.ShouldBe(AzureNaming.Subscription("prod-", longEndpoint));
        AzureNaming.Subscription("prod-", longEndpoint + "y").ShouldNotBe(subscription);
        AzureNaming.Queue("prod-", longEndpoint).ShouldBe("prod-" + longEndpoint);   // queues allow 260
    }

    [Fact]
    [Trait("Id", "UT-ASB-10")]
    [Trait("Covers", "doc 07 4;doc 07 7")]
    public void TypeFilters_BecomeCorrelationRules_PrefixesSqlRules_AndTheRestMatchesAll()
    {
        IReadOnlyList<CreateRuleOptions> types = AzureFilter.Rules(CloudEventFilter.ForTypes(["payments.settled.v1", "payments.failed.v2"]));
        IReadOnlyList<CreateRuleOptions> prefixes = AzureFilter.Rules("type LIKE 'payments.%'");
        IReadOnlyList<CreateRuleOptions> other = AzureFilter.Rules("sftenant = 'eu'");

        types.Select(r => AzureFilter.Describe(r.Filter)).ShouldBe(["type=payments.failed.v2", "type=payments.settled.v1"]);
        types.ShouldAllBe(r => r.Filter is CorrelationRuleFilter);
        prefixes.Single().Filter.ShouldBeOfType<SqlRuleFilter>().SqlExpression.ShouldBe("[cloudEvents:type] LIKE 'payments.%'");
        other.Single().Name.ShouldBe(AzureFilter.AllRuleName);
        AzureFilter.Rules((string?)null).Single().Filter.ShouldBeOfType<TrueRuleFilter>();
        AzureFilter.IsNative(CloudEventFilter.Parse("sftenant = 'eu'")).ShouldBeFalse();
        AzureFilter.IsNative(CloudEventFilter.Parse("type = 'a.v1'")).ShouldBeTrue();
        AzureFilter.Rules("type LIKE 'a_b.%'").Single().Name.ShouldBe(AzureFilter.AllRuleName);   // '_' is a LIKE wildcard
    }

    [Fact]
    [Trait("Id", "UT-ASB-11")]
    [Trait("Covers", "ADR-017;doc 07 5")]
    public void RuleComparison_IgnoresNamesOrderAndSpacing_ButNotContent()
    {
        RuleFilter[] expected = [new CorrelationRuleFilter { ApplicationProperties = { ["cloudEvents:type"] = "a.v1" } }, new SqlRuleFilter("[cloudEvents:type] LIKE 'b.%'")];
        RuleFilter[] same = [new SqlRuleFilter("[cloudEvents:type]  LIKE 'b.%'"), new CorrelationRuleFilter { ApplicationProperties = { ["cloudEvents:type"] = "a.v1" } }];

        AzureFilter.Equivalent(expected, same).ShouldBeTrue();
        AzureFilter.Equivalent([new TrueRuleFilter()], [new SqlRuleFilter("1=1")]).ShouldBeTrue();
        AzureFilter.Equivalent(expected, [new CorrelationRuleFilter { ApplicationProperties = { ["cloudEvents:type"] = "drifted.v1" } }]).ShouldBeFalse();
        AzureFilter.Describe(new CorrelationRuleFilter { Subject = "x", ApplicationProperties = { ["cloudEvents:type"] = "a.v1" } }).ShouldStartWith("other:");
    }

    // ================================================================ sending

    [Fact]
    [Trait("Id", "UT-ASB-12")]
    [Trait("Covers", "doc 07 4")]
    public async Task Sends_AreSplitIntoBatchesOfAHundred_InOrder()
    {
        var mocks = new AzureMocks();
        MessageEnvelope[] messages = [.. Enumerable.Range(0, 250).Select(i => AzureMocks.Envelope("k", $"{{\"n\":{i}}}"))];

        await mocks.Transport.SendAsync("orders", messages, None);

        mocks.Batches.Select(b => b.Messages.Count).ShouldBe([100, 100, 50]);
        mocks.Batches.ShouldAllBe(b => b.Entity == "orders");
        mocks.Batches.SelectMany(b => b.Messages).Select(m => m.MessageId).ShouldBe(messages.Select(m => m.MessageId.ToString()));
    }

    [Fact]
    [Trait("Id", "UT-ASB-13")]
    [Trait("Covers", "doc 07 4;NFR-PERF-07")]
    public async Task Batches_StayWithinTheSizeLimit_AndOversizedMessagesAreRefusedBeforeSending()
    {
        var mocks = new AzureMocks();
        MessageEnvelope[] large = [.. Enumerable.Range(0, 5).Select(_ => AzureMocks.Envelope() with { Body = new byte[100 * 1024] })];
        await mocks.Transport.PublishAsync("orders-events", large, None);
        mocks.Batches.Select(b => b.Messages.Count).ShouldBe([2, 2, 1]);
        mocks.Batches.ShouldAllBe(b => b.Entity == "orders-events");

        mocks.Batches.Clear();
        MessageEnvelope first = AzureMocks.Envelope();
        MessageEnvelope tooLarge = AzureMocks.Envelope() with { Body = new byte[AzureWire.MaxBodyBytes + 1] };
        MessageEnvelope last = AzureMocks.Envelope();
        TransportSendException refused = await Should.ThrowAsync<TransportSendException>(() => mocks.Transport.SendAsync("orders", [first, tooLarge, last], None).AsTask());

        refused.Failures.Single().MessageId.ShouldBe(tooLarge.MessageId);
        refused.Failures[0].Error.ShouldBeOfType<MessageTooLargeException>();
        refused.Sent.ShouldBe(2);
        refused.IsTransient.ShouldBeFalse();

        mocks.BatchCapacity = 64 * 1024;   // a broker that refuses what the body limit allows (properties count too)
        MessageEnvelope refusedByBroker = AzureMocks.Envelope() with { Body = new byte[100 * 1024] };
        (await Should.ThrowAsync<TransportSendException>(() => mocks.Transport.SendAsync("orders", [refusedByBroker], None).AsTask())).Failures.Single().Error.ShouldBeOfType<MessageTooLargeException>();
    }

    [Fact]
    [Trait("Id", "UT-ASB-14")]
    [Trait("Covers", "doc 07 4")]
    public async Task AFailedCall_FailsTheRest_AndAMissingEntityIsDestinationNotFound()
    {
        var mocks = new AzureMocks();
        ServiceBusSender sender = mocks.Sender("orders");
        int calls = 0;
        sender.SendMessagesAsync(Arg.Any<ServiceBusMessageBatch>(), Arg.Any<CancellationToken>()).Returns(_ => ++calls == 1
            ? Task.CompletedTask
            : Task.FromException(new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy)));
        MessageEnvelope[] many = [.. Enumerable.Range(0, 150).Select(_ => AzureMocks.Envelope())];

        TransportSendException partial = await Should.ThrowAsync<TransportSendException>(() => mocks.Transport.SendAsync("orders", many, None).AsTask());

        partial.Sent.ShouldBe(100);
        partial.Failures.Select(f => f.MessageId).ShouldBe(many.Skip(100).Select(m => m.MessageId));
        partial.IsTransient.ShouldBeTrue();

        mocks.Sender("missing").CreateMessageBatchAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new ServiceBusException("gone", ServiceBusFailureReason.MessagingEntityNotFound));
        (await Should.ThrowAsync<DestinationNotFoundException>(() => mocks.Transport.PublishAsync("missing", [AzureMocks.Envelope()], None).AsTask())).Destination.ShouldBe("missing");
    }

    [Fact]
    [Trait("Id", "UT-ASB-15")]
    [Trait("Covers", "ADR-033;doc 07 4")]
    public void ServiceBusErrors_MapToTypedTransportErrors()
    {
        AzureErrors.Map(new ServiceBusException("gone", ServiceBusFailureReason.MessagingEntityNotFound), "orders").ShouldBeOfType<DestinationNotFoundException>();
        AzureErrors.Map(new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy), "orders").ShouldBeOfType<TransportThrottledException>().RetryAfter.ShouldBe(TimeSpan.FromSeconds(10));
        AzureErrors.Map(new ServiceBusException("down", ServiceBusFailureReason.ServiceCommunicationProblem), "orders").ShouldBeOfType<TransportUnavailableException>();
        AzureErrors.Map(new ServiceBusException("slow", ServiceBusFailureReason.ServiceTimeout), "orders")!.ShouldBeOfType<TransportUnavailableException>();
        ((TransportException)AzureErrors.Map(new ServiceBusException("big", ServiceBusFailureReason.MessageSizeExceeded), "orders")!).IsTransient.ShouldBeFalse();
        AzureErrors.Map(new global::Azure.RequestFailedException(404, "no queue"), "orders").ShouldBeOfType<DestinationNotFoundException>();
        AzureErrors.Map(new global::Azure.RequestFailedException(429, "slow down"), "orders").ShouldBeOfType<TransportThrottledException>();
        ((TransportException)AzureErrors.Map(new global::Azure.RequestFailedException(403, "denied"), "orders")!).IsTransient.ShouldBeFalse();
        AzureErrors.Map(new AuthenticationFailedException("no token"), "orders").ShouldBeOfType<TransportUnavailableException>();
        AzureErrors.Map(new OperationCanceledException(), "orders").ShouldBeNull();
        AzureErrors.IsLockLost(new ServiceBusException("lost", ServiceBusFailureReason.MessageLockLost)).ShouldBeTrue();
        AzureErrors.IsLockLost(new ServiceBusException("lost", ServiceBusFailureReason.SessionLockLost)).ShouldBeTrue();
    }

    // ================================================================ receiving and settlement

    [Fact]
    [Trait("Id", "UT-ASB-16")]
    [Trait("Covers", "doc 07 4;doc 07 7;RX-R5")]
    public async Task Receive_CountsDeliveriesAcrossRetries_FiltersClientSide_AndDeadLettersWhatIsNotACloudEvent()
    {
        var mocks = new AzureMocks(queues: "orders");
        MessageEnvelope command = AzureMocks.Envelope(type: "orders.cancel");
        MessageEnvelope eu = AzureMocks.Envelope() with { TenantId = "eu" };
        MessageEnvelope us = AzureMocks.Envelope() with { TenantId = "us" };
        ServiceBusReceivedMessage usMessage = AzureMocks.Message(us, topic: "orders-events");
        ServiceBusReceivedMessage malformed = ServiceBusModelFactory.ServiceBusReceivedMessage(BinaryData.FromString("{}"), "x", lockTokenGuid: Guid.CreateVersion7(), deliveryCount: 1);
        mocks.Deliver(AzureMocks.Message(command, deliveryCount: 2, previous: 3), AzureMocks.Message(eu, topic: "orders-events"), usMessage, malformed);
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", ["orders-events"])
        {
            Filters = new Dictionary<string, string> { ["orders-events"] = "sftenant = 'eu'" },
        }, None);

        IReadOnlyList<ReceivedMessage> received = await receiver.ReceiveAsync(10, TimeSpan.Zero, None);

        received.Select(r => r.Envelope.MessageId).ShouldBe([command.MessageId, eu.MessageId]);
        received[0].DeliveryCount.ShouldBe(5);   // 3 before a delayed retry re-sent it, 2 since
        await mocks.Receiver.Received(1).CompleteMessageAsync(usMessage, Arg.Any<CancellationToken>());
        await mocks.Receiver.Received(1).DeadLetterMessageAsync(malformed, AzureReceiverBase.MalformedReason, Arg.Any<string>(), Arg.Any<CancellationToken>());
        mocks.ReceiverOptions.Single().Options.PrefetchCount.ShouldBe(0);
    }

    [Fact]
    [Trait("Id", "UT-ASB-17")]
    [Trait("Covers", "doc 04 9.2;doc 07 4")]
    public async Task Abandon_WithADelay_SchedulesACopyAndCompletes_WithoutOneAbandonsNatively()
    {
        var mocks = new AzureMocks(queues: "orders");
        ServiceBusReceivedMessage delayed = AzureMocks.Message(AzureMocks.Envelope(), deliveryCount: 2);
        ServiceBusReceivedMessage now = AzureMocks.Message(AzureMocks.Envelope());
        mocks.Deliver(delayed, now);
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []), None);
        IReadOnlyList<ReceivedMessage> received = await receiver.ReceiveAsync(10, TimeSpan.Zero, None);
        DateTimeOffset before = TimeProvider.System.GetUtcNow();

        await received[0].AbandonAsync(TimeSpan.FromMinutes(5), None);
        await received[1].AbandonAsync(TimeSpan.Zero, None);

        await mocks.Sender("orders").Received(1).ScheduleMessageAsync(
            Arg.Is<ServiceBusMessage>(m => (int)m.ApplicationProperties[AzureWire.DeliveriesProperty] == 2 && m.MessageId.EndsWith("/r2", StringComparison.Ordinal)),
            Arg.Is<DateTimeOffset>(t => t >= before.AddMinutes(5) && t < before.AddMinutes(6)), Arg.Any<CancellationToken>());
        await mocks.Receiver.Received(1).CompleteMessageAsync(delayed, Arg.Any<CancellationToken>());
        await mocks.Receiver.Received(1).AbandonMessageAsync(now, Arg.Any<IDictionary<string, object>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Id", "UT-ASB-18")]
    [Trait("Covers", "doc 07 4")]
    public async Task Renew_RenewsTheBrokerLock_ALostLockIsReported_AndALateSettlementIsIgnored()
    {
        var mocks = new AzureMocks(queues: "orders");
        ServiceBusReceivedMessage message = AzureMocks.Message(AzureMocks.Envelope());
        mocks.Deliver(message);
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []), None);
        ReceivedMessage held = (await receiver.ReceiveAsync(1, TimeSpan.Zero, None)).Single();

        await held.RenewAsync(None);
        await mocks.Receiver.Received(1).RenewMessageLockAsync(message, Arg.Any<CancellationToken>());

        mocks.Receiver.RenewMessageLockAsync(message, Arg.Any<CancellationToken>()).ThrowsAsync(new ServiceBusException("lost", ServiceBusFailureReason.MessageLockLost));
        mocks.Receiver.CompleteMessageAsync(message, Arg.Any<CancellationToken>()).ThrowsAsync(new ServiceBusException("lost", ServiceBusFailureReason.MessageLockLost));
        TransportException lost = await Should.ThrowAsync<TransportException>(() => held.RenewAsync(None).AsTask());
        lost.IsTransient.ShouldBeFalse();
        lost.Message.ShouldContain("lock was lost");
        await Should.NotThrowAsync(() => held.CompleteAsync(None).AsTask());   // the message was redelivered; nothing to do
    }

    [Fact]
    [Trait("Id", "UT-ASB-19")]
    [Trait("Covers", "doc 07 4")]
    public async Task ALockLongerThanTheQueues_IsKeptByRenewingTheBrokerLock()
    {
        var mocks = new AzureMocks(queues: "orders");
        ServiceBusReceivedMessage message = AzureMocks.Message(AzureMocks.Envelope(), lockedUntil: TimeProvider.System.GetUtcNow().AddSeconds(1));
        mocks.Deliver(message);
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []) { LockDuration = TimeSpan.FromMinutes(2) }, None);
        _ = await receiver.ReceiveAsync(1, TimeSpan.Zero, None);

        await Task.Delay(TimeSpan.FromSeconds(1.5), TimeProvider.System, TestContext.Current.CancellationToken);

        await mocks.Receiver.Received().RenewMessageLockAsync(message, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Id", "UT-ASB-20")]
    [Trait("Covers", "GAP-11;doc 07 4")]
    public async Task TheDeadLetterReceiver_ReadsTheBuiltInQueue_WithTheBrokersReason()
    {
        var mocks = new AzureMocks(queues: "orders");
        MessageEnvelope exhausted = AzureMocks.Envelope();
        MessageEnvelope poison = AzureMocks.Envelope();
        ServiceBusReceivedMessage malformed = ServiceBusModelFactory.ServiceBusReceivedMessage(BinaryData.FromString("{}"), "x", lockTokenGuid: Guid.CreateVersion7(), deliveryCount: 1);
        mocks.Deliver(AzureMocks.Message(exhausted), AzureMocks.Message(poison, deadLetterReason: "tck-poison"), malformed);
        await using ITransportReceiver deadLetters = await mocks.Transport.CreateDeadLetterReceiverAsync("orders", None);

        IReadOnlyList<ReceivedMessage> dead = await deadLetters.ReceiveAsync(10, TimeSpan.Zero, None);

        mocks.ReceiverOptions.Single().Options.SubQueue.ShouldBe(SubQueue.DeadLetter);
        dead.Select(d => d.DeadLetterReason).ShouldBe([AzureReceiverBase.MaxDeliveriesReason, "tck-poison"]);
        await mocks.Receiver.Received(1).DeferMessageAsync(malformed, Arg.Any<IDictionary<string, object>>(), Arg.Any<CancellationToken>());   // kept for inspection, out of the way
        await Should.ThrowAsync<TransportException>(() => dead[0].DeadLetterAsync("again", None).AsTask());
        await Should.ThrowAsync<DestinationNotFoundException>(() => mocks.Transport.CreateDeadLetterReceiverAsync("billing", None).AsTask());
    }

    [Fact]
    [Trait("Id", "UT-ASB-20")]
    [Trait("Covers", "doc 07 4")]
    public async Task Dispose_ReleasesUnsettledMessagesAtOnce_NeverWhatWasCompleted()
    {
        var mocks = new AzureMocks(queues: "orders");
        ServiceBusReceivedMessage done = AzureMocks.Message(AzureMocks.Envelope());
        ServiceBusReceivedMessage held = AzureMocks.Message(AzureMocks.Envelope());
        mocks.Deliver(done, held);
        ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []), None);
        IReadOnlyList<ReceivedMessage> received = await receiver.ReceiveAsync(10, TimeSpan.Zero, None);
        await received[0].CompleteAsync(None);

        await receiver.DisposeAsync();

        await mocks.Receiver.Received(1).AbandonMessageAsync(held, Arg.Any<IDictionary<string, object>>(), Arg.Any<CancellationToken>());
        await mocks.Receiver.DidNotReceive().AbandonMessageAsync(done, Arg.Any<IDictionary<string, object>>(), Arg.Any<CancellationToken>());
        await mocks.Receiver.Received(1).DisposeAsync();
    }

    // ================================================================ health and registration

    [Fact]
    [Trait("Id", "UT-ASB-25")]
    [Trait("Covers", "doc 07 6")]
    public async Task HealthCheck_ReportsWhetherTheEndpointQueueIsReachable()
    {
        var mocks = new AzureMocks(queues: "orders");
        var down = new AzureMocks();
        down.Admin.QueueExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new global::Azure.RequestFailedException(503, "down"));

        (await new AzureTransportHealthCheck(mocks.Transport, "orders").CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Healthy);
        (await new AzureTransportHealthCheck(mocks.Transport, "billing").CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
        (await new AzureTransportHealthCheck(down.Transport, "orders").CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    [Trait("Id", "UT-ASB-25")]
    [Trait("Covers", "doc 07 6")]
    public async Task UseAzureTransport_RegistersTheTransport_WithATokenCredential_AndTheHealthCheck()
    {
        TokenCredential credential = Substitute.For<TokenCredential>();
        var services = new ServiceCollection();
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "orders";
            sf.UseInMemoryStorage();
            sf.UseAzureTransport(o =>
            {
                o.FullyQualifiedNamespace = "acme.servicebus.windows.net";
                o.Credential = credential;
            });
        });
        services.AddHealthChecks().AddSourceFlowAzure();

        await using ServiceProvider provider = services.BuildServiceProvider();

        AzureTransport transport = provider.GetRequiredService<ITransport>().ShouldBeOfType<AzureTransport>();
        transport.SystemName.ShouldBe("servicebus");
        transport.Client.FullyQualifiedNamespace.ShouldBe("acme.servicebus.windows.net");
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value.Registrations.ShouldContain(r => r.Name == "sourceflow-azure");
    }

    private static void Copy(AzureTransportOptions from, AzureTransportOptions to)
    {
        to.FullyQualifiedNamespace = from.FullyQualifiedNamespace;
        to.ConnectionString = from.ConnectionString;
        to.AdministrationConnectionString = from.AdministrationConnectionString;
        to.MaxRetries = from.MaxRetries;
        to.TryTimeout = from.TryTimeout;
        to.LockDuration = from.LockDuration;
        to.MessageTimeToLive = from.MessageTimeToLive;
        to.DuplicateDetectionWindow = from.DuplicateDetectionWindow;
        to.MaxConcurrentSessions = from.MaxConcurrentSessions;
        to.NamePrefix = from.NamePrefix;
    }

    private sealed class CapturingLoggerProvider : ILoggerFactory
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            return new Capturing(Messages);
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Capturing(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
