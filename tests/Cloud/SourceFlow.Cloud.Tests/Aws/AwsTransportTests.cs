using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SourceFlow.Cloud.Aws;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Tests.Aws;

/// <summary>UT-AWS-01..30 that need no broker: options, wire mapping, naming, filters, batching, settlement, errors.</summary>
[Trait("Cloud", "Aws")]
public sealed class AwsTransportTests
{
    // ================================================================ options (doc 07 §6, GAP-03)

    [Fact]
    [Trait("Id", "UT-AWS-01")]
    [Trait("Covers", "doc 07 6")]
    public void Options_Defaults_AreValid_AndMatchTheSdkLimits()
    {
        var options = new AwsTransportOptions();

        Should.NotThrow(options.Validate);
        options.MaxSdkRetries.ShouldBe(3);
        options.ReceiveWaitTime.ShouldBe(TimeSpan.FromSeconds(20));
        options.MessageRetention.ShouldBe(TimeSpan.FromDays(14));
        options.NamePrefix.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Id", "UT-AWS-02")]
    [Trait("Covers", "doc 07 6;GAP-03")]
    [InlineData("region")]
    [InlineData("retries")]
    [InlineData("timeout")]
    [InlineData("wait")]
    [InlineData("retention")]
    [InlineData("prefix")]
    public void Options_ThatCannotBeHonoured_FailValidation(string option)
    {
        var options = new AwsTransportOptions();
        switch (option)
        {
            case "region": options.Region = "mars-north-1"; break;
            case "retries": options.MaxSdkRetries = 11; break;
            case "timeout": options.Timeout = TimeSpan.Zero; break;
            case "wait": options.ReceiveWaitTime = TimeSpan.FromSeconds(21); break;
            case "retention": options.MessageRetention = TimeSpan.FromDays(15); break;
            case "prefix": options.NamePrefix = "prod."; break;
        }

        Should.Throw<ArgumentException>(options.Validate);
        Should.Throw<ArgumentException>(() => new SourceFlowBuilderProbe().UseAws(o => Copy(options, o)));
    }

    [Fact]
    [Trait("Id", "UT-AWS-03")]
    [Trait("Covers", "doc 07 6;GAP-03")]
    public void Options_MapToTheSdkClientConfig_AndToAwsOptions()
    {
        var options = new AwsTransportOptions { Region = "eu-west-1", Profile = "ops", ServiceUrl = new Uri("http://localhost:4566"), MaxSdkRetries = 5, Timeout = TimeSpan.FromSeconds(12) };

        foreach (ClientConfig config in new ClientConfig[] { options.ToSqsConfig(), options.ToSnsConfig() })
        {
            config.MaxErrorRetry.ShouldBe(5);
            config.Timeout.ShouldBe(TimeSpan.FromSeconds(12));
            config.ServiceURL.ShouldBe("http://localhost:4566/");
            config.AuthenticationRegion.ShouldBe("eu-west-1");
        }

        Amazon.Extensions.NETCore.Setup.AWSOptions aws = options.ToAwsOptions();
        aws.Region.ShouldBe(RegionEndpoint.EUWest1);
        aws.Profile.ShouldBe("ops");
        aws.DefaultClientConfig.MaxErrorRetry.ShouldBe(5);
        aws.DefaultClientConfig.Timeout.ShouldBe(TimeSpan.FromSeconds(12));
        aws.DefaultClientConfig.ServiceURL.ShouldBe("http://localhost:4566/");
    }

    [Fact]
    [Trait("Id", "UT-AWS-12")]
    [Trait("Covers", "GAP-03;doc 07 6")]
    public void Region_IsHonoured_ByBothClients()
    {
        using var transport = new AwsTransport(new AwsTransportOptions { Region = "eu-central-1", MaxSdkRetries = 1, Timeout = TimeSpan.FromSeconds(7) });

        transport.Sqs.Config.RegionEndpoint.ShouldBe(RegionEndpoint.EUCentral1);
        transport.Sns.Config.RegionEndpoint.ShouldBe(RegionEndpoint.EUCentral1);
        transport.Sqs.Config.MaxErrorRetry.ShouldBe(1);
        transport.Sns.Config.Timeout.ShouldBe(TimeSpan.FromSeconds(7));
    }

    [Fact]
    [Trait("Id", "UT-AWS-13")]
    [Trait("Covers", "doc 07 6")]
    public void AMissingProfile_FailsAtConstruction()
    {
        var options = new AwsTransportOptions { Region = "eu-west-1", Profile = "sourceflow-no-such-profile-" + Guid.CreateVersion7().ToString("N") };

        Should.Throw<ArgumentException>(() => new AwsTransport(options)).Message.ShouldContain("was not found");
    }

    // ================================================================ wire (CloudEvents binary mode)

    [Fact]
    [Trait("Id", "UT-AWS-06")]
    [Trait("Covers", "ADR-033")]
    public void Encode_UsesCeAttributes_WithinTheTenAttributeLimit()
    {
        MessageEnvelope envelope = AwsMocks.Envelope("k") with { Subject = "s", CorrelationId = new CorrelationId("c"), Actor = "a", TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" };

        AwsOutgoingMessage wire = AwsWire.Encode(envelope);

        wire.Attributes.Count.ShouldBeLessThanOrEqualTo(10);
        wire.Attributes.Keys.ShouldAllBe(k => k.StartsWith("ce_", StringComparison.Ordinal));
        wire.Attributes["ce_type"].ShouldBe("orders.placed.v1");
        wire.Body.ShouldBe("{\"n\":1}");
        AwsWire.Decode(wire.Attributes, wire.Body).ShouldBe(envelope);
    }

    [Fact]
    [Trait("Id", "UT-AWS-07")]
    [Trait("Covers", "ADR-033")]
    public void Encode_OverflowsExtensionsIntoSfext_LeavingRoomForTheTransportAttributes()
    {
        MessageEnvelope envelope = AwsMocks.Envelope("k") with
        {
            Subject = "s",
            CorrelationId = new CorrelationId("c"),
            CausationId = new MessageId(Guid.CreateVersion7()),
            StreamVersion = 7,
            Actor = "a",
            TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            Headers = new Dictionary<string, string> { ["x"] = "y" },
            Body = new byte[] { 0xFF, 0x00 },
        };

        AwsOutgoingMessage wire = AwsWire.Encode(envelope, topic: "orders-events", reason: "poison");

        wire.Attributes.Count.ShouldBe(10);
        wire.Attributes.ShouldContainKey("ce_sfext");
        wire.Attributes["sf_topic"].ShouldBe("orders-events");
        wire.Attributes["sf_reason"].ShouldBe("poison");
        wire.Attributes["sf_body"].ShouldBe("base64");
        AwsWire.Decode(wire.Attributes, wire.Body).ShouldBe(envelope);
    }

    [Theory]
    [Trait("Id", "UT-AWS-08")]
    [Trait("Covers", "ADR-033")]
    [InlineData(new byte[] { 0x7B, 0x7D }, null)]                        // JSON text
    [InlineData(new byte[] { 0xC3, 0xA9, 0xF0, 0x9F, 0x98, 0x80 }, null)] // é and an emoji
    [InlineData(new byte[] { 0xFF, 0xFE }, "base64")]                    // not UTF-8
    [InlineData(new byte[] { 0x41, 0x00, 0x42 }, "base64")]              // NUL is not an SQS character
    [InlineData(new byte[] { 0xEF, 0xBF, 0xBF }, "base64")]              // U+FFFF is not an SQS character
    [InlineData(new byte[0], "empty")]
    public void Bodies_TravelAsText_OnlyWhenSqsCarriesThemUnchanged(byte[] body, string? encoding)
    {
        MessageEnvelope envelope = AwsMocks.Envelope() with { Body = body, ContentType = "application/octet-stream" };

        AwsOutgoingMessage wire = AwsWire.Encode(envelope);

        wire.Attributes.GetValueOrDefault("sf_body").ShouldBe(encoding);
        wire.Body.ShouldNotBeEmpty();
        AwsWire.Decode(wire.Attributes, wire.Body).Body.ToArray().ShouldBe(body);
    }

    [Fact]
    [Trait("Id", "UT-AWS-09")]
    [Trait("Covers", "ADR-033")]
    public void Decode_RejectsAMessageThatIsNotASourceFlowCloudEvent()
    {
        AwsOutgoingMessage wire = AwsWire.Encode(AwsMocks.Envelope());

        Should.Throw<FormatException>(() => AwsWire.Decode(new Dictionary<string, string>(wire.Attributes) { ["sf_body"] = "gzip" }, wire.Body));
        Should.Throw<FormatException>(() => AwsWire.Decode(wire.Attributes.Where(a => a.Key != "ce_type").ToDictionary(), wire.Body));
        Should.Throw<FormatException>(() => AwsWire.Decode(new Dictionary<string, string>(), "{\"Type\":\"Notification\"}"));
    }

    [Fact]
    [Trait("Id", "UT-AWS-10")]
    [Trait("Covers", "doc 07 4")]
    public void FifoGroup_IsThePartitionKey_OrTheIdWithoutOne_AndHashedWhenSqsWouldRejectIt()
    {
        MessageEnvelope keyed = AwsMocks.Envelope("account-42");
        MessageEnvelope bySubject = AwsMocks.Envelope() with { Subject = "account-7" };
        MessageEnvelope none = AwsMocks.Envelope();
        MessageEnvelope unicode = AwsMocks.Envelope("компания");
        MessageEnvelope longKey = AwsMocks.Envelope(new string('k', 200));

        AwsWire.GroupId(keyed).ShouldBe("account-42");
        AwsWire.GroupId(bySubject).ShouldBe("account-7");
        AwsWire.GroupId(none).ShouldBe(none.MessageId.ToString());
        AwsWire.GroupId(unicode).ShouldStartWith("k-");
        AwsWire.GroupId(unicode).ShouldBe(AwsWire.GroupId(AwsMocks.Envelope("компания")));
        AwsWire.GroupId(longKey).Length.ShouldBeLessThanOrEqualTo(128);
    }

    [Fact]
    [Trait("Id", "UT-AWS-11")]
    [Trait("Covers", "doc 07 4")]
    public async Task FifoQueues_GetGroupAndDeduplicationIds_StandardQueuesDoNot()
    {
        var mocks = new AwsMocks("orders.fifo", "audit");
        MessageEnvelope message = AwsMocks.Envelope("account-42");

        await mocks.Transport.SendAsync("orders", [message], CancellationToken.None);
        await mocks.Transport.SendAsync("audit", [message], CancellationToken.None);

        SendMessageBatchRequestEntry fifo = mocks.Batches[0].Entries.Single();
        fifo.MessageGroupId.ShouldBe("account-42");
        fifo.MessageDeduplicationId.ShouldBe(message.MessageId.ToString());
        mocks.Batches[0].QueueUrl.ShouldBe(AwsMocks.Url("orders.fifo"));
        SendMessageBatchRequestEntry standard = mocks.Batches[1].Entries.Single();
        standard.MessageGroupId.ShouldBeNull();
        standard.MessageDeduplicationId.ShouldBeNull();
    }

    // ================================================================ naming

    [Fact]
    [Trait("Id", "UT-AWS-14")]
    [Trait("Covers", "RT-R5")]
    public void Names_AreSanitisedAndPrefixed_WithFifoAndDeadLetterSuffixes()
    {
        AwsNaming.Queue("prod-", "bank.accounts", ordered: false).ShouldBe("prod-bank-accounts");
        AwsNaming.Queue("prod-", "bank.accounts", ordered: true).ShouldBe("prod-bank-accounts.fifo");
        AwsNaming.DeadLetterQueue("prod-", "bank.accounts", ordered: false).ShouldBe("prod-bank-accounts-dlq");
        AwsNaming.DeadLetterQueue("prod-", "bank.accounts", ordered: true).ShouldBe("prod-bank-accounts-dlq.fifo");
        AwsNaming.Topic("", "accounts/events v1", ordered: false).ShouldBe("accounts-events-v1");
        AwsNaming.NameOfArn("arn:aws:sns:eu-west-1:123456789012:prod-orders.fifo").ShouldBe("prod-orders.fifo");
    }

    [Fact]
    [Trait("Id", "UT-AWS-15")]
    [Trait("Covers", "RT-R5")]
    public void LongNames_FitTheSqsLimit_AndStayDeterministic()
    {
        string logical = "a-very-long-service-name-that-goes-on." + new string('x', 120);

        string name = AwsNaming.DeadLetterQueue("staging-", logical, ordered: true);

        name.Length.ShouldBeLessThanOrEqualTo(80);
        name.ShouldEndWith("-dlq.fifo");
        name.ShouldBe(AwsNaming.DeadLetterQueue("staging-", logical, ordered: true));
        AwsNaming.Base("staging-", logical).ShouldNotBe(AwsNaming.Base("staging-", logical + "y"));
    }

    // ================================================================ filters

    [Fact]
    [Trait("Id", "UT-AWS-16")]
    [Trait("Covers", "ADR-033")]
    public void TypeFilters_BecomeAnSnsFilterPolicyOnCeType()
    {
        string policy = AwsFilterPolicy.Translate(CloudEventFilter.ForTypes(["b.v1", "a.v2"]))!;

        policy.ShouldBe("{\"ce_type\":[\"a.v2\",\"b.v1\"]}");
        AwsFilterPolicy.Translate("type = 'a.v1' OR type = 'b.v1'").ShouldBe("{\"ce_type\":[\"a.v1\",\"b.v1\"]}");
    }

    [Fact]
    [Trait("Id", "UT-AWS-17")]
    [Trait("Covers", "ADR-033")]
    public void PrefixFilters_BecomeSnsPrefixMatches()
    {
        AwsFilterPolicy.Translate("type LIKE 'orders.%' OR type LIKE 'billing.%'").ShouldBe("{\"ce_type\":[{\"prefix\":\"billing.\"},{\"prefix\":\"orders.\"}]}");
    }

    [Theory]
    [Trait("Id", "UT-AWS-18")]
    [Trait("Covers", "ADR-033")]
    [InlineData("type LIKE 'orders.%' AND sftenant = 'eu'")]
    [InlineData("EXISTS sfactor")]
    [InlineData("type LIKE '%placed%'")]
    [InlineData("NOT (type = 'a.v1')")]
    public void OtherFilters_HaveNoPolicy_AndAreEvaluatedByTheReceiver(string filter)
    {
        AwsFilterPolicy.Translate(filter).ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "UT-AWS-19")]
    [Trait("Covers", "ADR-017")]
    public void PolicyComparison_IgnoresOrderAndWhitespace_ButNotContent()
    {
        AwsFilterPolicy.Equivalent("{\"ce_type\":[\"a\",\"b\"]}", "{ \"ce_type\" : [ \"b\", \"a\" ] }").ShouldBeTrue();
        AwsFilterPolicy.Equivalent(null, "").ShouldBeTrue();
        AwsFilterPolicy.Equivalent("{\"ce_type\":[\"a\"]}", "{\"ce_type\":[\"b\"]}").ShouldBeFalse();
        AwsFilterPolicy.Equivalent("{\"ce_type\":[\"a\"]}", null).ShouldBeFalse();
        AwsFilterPolicy.Equivalent("{\"ce_type\":[\"a\"]}", "not json").ShouldBeFalse();
    }

    // ================================================================ sending

    [Fact]
    [Trait("Id", "UT-AWS-20")]
    [Trait("Covers", "ADR-033")]
    public async Task Sends_AreSplitIntoBatchesOfTen_InOrder()
    {
        var mocks = new AwsMocks("orders");
        MessageEnvelope[] messages = [.. Enumerable.Range(0, 25).Select(i => AwsMocks.Envelope(body: $"{{\"n\":{i}}}"))];

        await mocks.Transport.SendAsync("orders", messages, CancellationToken.None);

        mocks.Batches.Select(b => b.Entries.Count).ShouldBe([10, 10, 5]);
        mocks.Batches.SelectMany(b => b.Entries).Select(e => e.MessageBody).ShouldBe(messages.Select(m => Encoding.UTF8.GetString(m.Body.Span)));
    }

    [Fact]
    [Trait("Id", "UT-AWS-21")]
    [Trait("Covers", "ADR-033")]
    public async Task Batches_StayWithinTheSizeLimit()
    {
        var mocks = new AwsMocks("orders");
        MessageEnvelope[] messages = [.. Enumerable.Range(0, 4).Select(_ => AwsMocks.Envelope(body: "\"" + new string('x', 100 * 1024) + "\""))];

        await mocks.Transport.SendAsync("orders", messages, CancellationToken.None);

        mocks.Batches.Select(b => b.Entries.Count).ShouldBe([2, 2]);
    }

    [Fact]
    [Trait("Id", "UT-AWS-22")]
    [Trait("Covers", "NFR-PERF-07")]
    public async Task OversizedMessages_AreRefusedBeforeSending_AndReportedAlone()
    {
        var mocks = new AwsMocks("orders");
        MessageEnvelope good = AwsMocks.Envelope();
        MessageEnvelope tooLarge = AwsMocks.Envelope() with { Body = new byte[AwsWire.MaxBodyBytes + 1] };
        MessageEnvelope hugeHeaders = AwsMocks.Envelope() with { Headers = new Dictionary<string, string> { ["h"] = new string('h', AwsWire.MaxMessageBytes) } };

        TransportSendException error = await Should.ThrowAsync<TransportSendException>(() => mocks.Transport.SendAsync("orders", [good, tooLarge, hugeHeaders], CancellationToken.None).AsTask());

        error.Failures.Select(f => f.MessageId).ShouldBe([tooLarge.MessageId, hugeHeaders.MessageId]);
        error.Failures.ShouldAllBe(f => f.Error is MessageTooLargeException);
        error.Sent.ShouldBe(1);
        error.IsTransient.ShouldBeFalse();
        mocks.Batches.Single().Entries.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "UT-AWS-23")]
    [Trait("Covers", "ADR-033")]
    public async Task FailedBatchEntries_AreReportedPerMessage_TransientUnlessTheSenderIsAtFault()
    {
        var mocks = new AwsMocks("orders");
        mocks.Sqs.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>()).Returns(new SendMessageBatchResponse
        {
            Successful = [new SendMessageBatchResultEntry { Id = "0" }],
            Failed =
            [
                new BatchResultErrorEntry { Id = "1", Code = "InternalError", Message = "try again", SenderFault = false },
                new BatchResultErrorEntry { Id = "2", Code = "InvalidMessageContents", Message = "bad", SenderFault = true },
            ],
        });
        MessageEnvelope[] messages = [AwsMocks.Envelope(), AwsMocks.Envelope(), AwsMocks.Envelope()];

        TransportSendException error = await Should.ThrowAsync<TransportSendException>(() => mocks.Transport.SendAsync("orders", messages, CancellationToken.None).AsTask());

        error.Sent.ShouldBe(1);
        error.Failures.Select(f => f.MessageId).ShouldBe([messages[1].MessageId, messages[2].MessageId]);
        ((TransportException)error.Failures[0].Error).IsTransient.ShouldBeTrue();
        ((TransportException)error.Failures[1].Error).IsTransient.ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-AWS-24")]
    [Trait("Covers", "ADR-033")]
    public void SdkErrors_MapToTypedTransportErrors()
    {
        AwsErrors.Map(new AmazonServiceException("slow down", ErrorType.Sender, "Throttling", "r", HttpStatusCode.BadRequest), "q")
            .ShouldBeOfType<TransportThrottledException>().RetryAfter.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.Zero);
        AwsErrors.Map(new AmazonServiceException("busy", ErrorType.Unknown, "Whatever", "r", HttpStatusCode.TooManyRequests), "q").ShouldBeOfType<TransportThrottledException>();
        AwsErrors.Map(new AmazonServiceException("down", ErrorType.Receiver, "InternalError", "r", HttpStatusCode.ServiceUnavailable), "q").ShouldBeOfType<TransportUnavailableException>().IsTransient.ShouldBeTrue();
        AwsErrors.Map(new QueueDoesNotExistException("gone"), "q").ShouldBeOfType<DestinationNotFoundException>().Destination.ShouldBe("q");
        AwsErrors.Map(new Amazon.SimpleNotificationService.Model.NotFoundException("gone"), "t").ShouldBeOfType<DestinationNotFoundException>();
        AwsErrors.Map(new HttpRequestException("refused", new SocketException()), "q").ShouldBeOfType<TransportUnavailableException>();
        AwsErrors.Map(new TaskCanceledException("timeout", new TimeoutException()), "q").ShouldBeOfType<TransportUnavailableException>();
        TransportException denied = AwsErrors.Map(new AmazonServiceException("no", ErrorType.Sender, "AccessDenied", "r", HttpStatusCode.Forbidden), "q").ShouldNotBeNull();
        denied.IsTransient.ShouldBeFalse();
        AwsErrors.Map(new OperationCanceledException(), "q").ShouldBeNull();
        AwsErrors.Map(new ArgumentException("bug"), "q").ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "UT-AWS-24")]
    [Trait("Covers", "ADR-033")]
    public async Task SendingToAMissingQueue_FailsWithDestinationNotFound()
    {
        var mocks = new AwsMocks();

        (await Should.ThrowAsync<DestinationNotFoundException>(() => mocks.Transport.SendAsync("nowhere", [AwsMocks.Envelope()], CancellationToken.None).AsTask())).Destination.ShouldBe("nowhere");
    }

    // ================================================================ receiving and settlement

    [Fact]
    [Trait("Id", "UT-AWS-25")]
    [Trait("Covers", "doc 07 6")]
    public async Task Receive_ShortPollsForSubSecondWaits_LongPollsUpToTheOption_AndLocksForTheLockDuration()
    {
        var mocks = new AwsMocks("orders");
        var requests = new List<ReceiveMessageRequest>();
        mocks.Sqs.ReceiveMessageAsync(Arg.Do<ReceiveMessageRequest>(requests.Add), Arg.Any<CancellationToken>()).Returns(new ReceiveMessageResponse());
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []) { LockDuration = TimeSpan.FromSeconds(45.2) }, CancellationToken.None);

        await receiver.ReceiveAsync(50, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await receiver.ReceiveAsync(3, TimeSpan.FromSeconds(2.5), CancellationToken.None);
        await receiver.ReceiveAsync(3, TimeSpan.FromMinutes(1), CancellationToken.None);

        requests.Select(r => r.WaitTimeSeconds).ShouldBe([0, 3, 20]);
        requests.Select(r => r.MaxNumberOfMessages).ShouldBe([10, 3, 3]);
        requests.ShouldAllBe(r => r.VisibilityTimeout == 46 && r.QueueUrl == AwsMocks.Url("orders"));
        requests[0].MessageAttributeNames.ShouldBe(["All"]);
    }

    [Fact]
    [Trait("Id", "UT-AWS-26")]
    [Trait("Covers", "doc 07 4")]
    public async Task Abandon_DelaysNatively_WithTheVisibilityTimeout_CappedAtTwelveHours()
    {
        var mocks = new AwsMocks("orders");
        var visibility = new List<int?>();
        mocks.Sqs.ChangeMessageVisibilityAsync(Arg.Do<ChangeMessageVisibilityRequest>(r => visibility.Add(r.VisibilityTimeout)), Arg.Any<CancellationToken>()).Returns(new ChangeMessageVisibilityResponse());
        mocks.Deliver(AwsMocks.Message(AwsMocks.Envelope()), AwsMocks.Message(AwsMocks.Envelope()));
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []), CancellationToken.None);
        IReadOnlyList<ReceivedMessage> received = await receiver.ReceiveAsync(10, TimeSpan.Zero, CancellationToken.None);

        await received[0].AbandonAsync(TimeSpan.FromSeconds(2.1), CancellationToken.None);
        await received[1].AbandonAsync(TimeSpan.FromDays(2), CancellationToken.None);

        visibility.ShouldBe([3, 43200]);
    }

    [Fact]
    [Trait("Id", "UT-AWS-27")]
    [Trait("Covers", "ADR-033")]
    public async Task Renew_ExtendsTheVisibilityByTheLock_AndALostLockIsReported()
    {
        var mocks = new AwsMocks("orders");
        var visibility = new List<int?>();
        mocks.Sqs.ChangeMessageVisibilityAsync(Arg.Do<ChangeMessageVisibilityRequest>(r => visibility.Add(r.VisibilityTimeout)), Arg.Any<CancellationToken>()).Returns(new ChangeMessageVisibilityResponse());
        mocks.Deliver(AwsMocks.Message(AwsMocks.Envelope()));
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []) { LockDuration = TimeSpan.FromSeconds(30) }, CancellationToken.None);
        ReceivedMessage message = (await receiver.ReceiveAsync(1, TimeSpan.Zero, CancellationToken.None)).Single();

        await message.RenewAsync(CancellationToken.None);
        visibility.ShouldBe([30]);

        mocks.LockLost();
        TransportException lost = await Should.ThrowAsync<TransportException>(() => message.RenewAsync(CancellationToken.None).AsTask());
        lost.IsTransient.ShouldBeFalse();
        lost.Message.ShouldContain("lock was lost");
    }

    [Fact]
    [Trait("Id", "UT-AWS-28")]
    [Trait("Covers", "ADR-033")]
    public async Task ALateSettlement_AfterTheLockExpired_IsIgnored()
    {
        var mocks = new AwsMocks("orders");
        mocks.Deliver(AwsMocks.Message(AwsMocks.Envelope(), receiveCount: 3));
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []), CancellationToken.None);
        ReceivedMessage message = (await receiver.ReceiveAsync(1, TimeSpan.Zero, CancellationToken.None)).Single();
        mocks.LockLost();

        message.DeliveryCount.ShouldBe(3);
        await Should.NotThrowAsync(() => message.CompleteAsync(CancellationToken.None).AsTask());
        await Should.NotThrowAsync(() => message.AbandonAsync(TimeSpan.Zero, CancellationToken.None).AsTask());
    }

    [Fact]
    [Trait("Id", "UT-AWS-29")]
    [Trait("Covers", "ADR-033;doc 07 7")]
    public async Task FiltersSnsCannotExpress_AreAppliedByTheReceiver_PerTopic_NeverToCommands()
    {
        var mocks = new AwsMocks("eu-orders");
        var deleted = new List<string>();
        mocks.Sqs.DeleteMessageAsync(Arg.Do<DeleteMessageRequest>(r => deleted.Add(r.ReceiptHandle)), Arg.Any<CancellationToken>()).Returns(new DeleteMessageResponse());
        MessageEnvelope euOrder = AwsMocks.Envelope() with { TenantId = "eu" };
        MessageEnvelope usOrder = AwsMocks.Envelope() with { TenantId = "us" };
        MessageEnvelope usBilling = AwsMocks.Envelope(type: "billing.sent") with { TenantId = "us" };
        MessageEnvelope command = AwsMocks.Envelope(type: "orders.cancel") with { TenantId = "us" };
        mocks.Deliver(AwsMocks.Message(euOrder, "orders"), AwsMocks.Message(usOrder, "orders"), AwsMocks.Message(usBilling, "billing"), AwsMocks.Message(command));
        var options = new ReceiverOptions("eu-orders", ["orders", "billing"])
        {
            Filters = new Dictionary<string, string> { ["orders"] = "sftenant = 'eu'", ["billing"] = "type = 'billing.sent.v1'" },
        };
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(options, CancellationToken.None);

        IReadOnlyList<ReceivedMessage> received = await receiver.ReceiveAsync(10, TimeSpan.Zero, CancellationToken.None);

        received.Select(r => r.Envelope.MessageId).ShouldBe([euOrder.MessageId, usBilling.MessageId, command.MessageId]);
        deleted.ShouldBe(["rh-" + usOrder.MessageId]);   // acknowledged unseen; the billing filter is native (SNS)
    }

    [Fact]
    [Trait("Id", "UT-AWS-30")]
    [Trait("Covers", "doc 07 6")]
    public async Task HealthCheck_ReportsWhetherTheEndpointQueueIsReachable()
    {
        var mocks = new AwsMocks("orders");
        var healthy = new AwsTransportHealthCheck(mocks.Transport, "orders");
        var missing = new AwsTransportHealthCheck(mocks.Transport, "billing");
        var down = new AwsMocks();
        down.Sqs.GetQueueUrlAsync(Arg.Any<GetQueueUrlRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("refused"));

        (await healthy.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Healthy);
        (await missing.CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
        (await new AwsTransportHealthCheck(down.Transport, "orders").CheckHealthAsync(new HealthCheckContext())).Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [Fact]
    [Trait("Id", "UT-AWS-30")]
    [Trait("Covers", "doc 07 6")]
    public void UseAwsTransport_RegistersTheTransport_AndTheHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "orders";
            sf.UseInMemoryStorage();
            sf.UseAwsTransport(o => o.Region = "eu-west-1");
        });
        services.AddHealthChecks().AddSourceFlowAws();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITransport>().ShouldBeOfType<AwsTransport>().SystemName.ShouldBe("aws_sqs");
        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value.Registrations.ShouldContain(r => r.Name == "sourceflow-aws");
    }

    private static void Copy(AwsTransportOptions from, AwsTransportOptions to)
    {
        to.Region = from.Region;
        to.MaxSdkRetries = from.MaxSdkRetries;
        to.Timeout = from.Timeout;
        to.ReceiveWaitTime = from.ReceiveWaitTime;
        to.MessageRetention = from.MessageRetention;
        to.NamePrefix = from.NamePrefix;
    }

    // UseAwsTransport validates when it is called, before the host starts.
    private sealed class SourceFlowBuilderProbe
    {
        public void UseAws(Action<AwsTransportOptions> configure)
        {
            new ServiceCollection().AddSourceFlow(sf =>
            {
                sf.ServiceName = "probe";
                sf.UseAwsTransport(configure);
            });
        }
    }
}
