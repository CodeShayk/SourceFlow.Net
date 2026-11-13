using System.Text;
using System.Text.Json.Nodes;
using Amazon.SQS.Model;
using SourceFlow.Cloud.Aws;
using SourceFlow.Messaging;
using SourceFlow.Migration;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Tests.Aws;

/// <summary>
/// The v2 wire bridge on the AWS receiver (doc 07 §8, WP-5.4) without a broker: reading the v2 attribute shape, mapped and
/// unmapped messages, and the bridge being off by default. The same cases run on LocalStack in the E2E suite (MIG-10..12).
/// </summary>
[Trait("Cloud", "Aws")]
public sealed class AwsV2BridgeTests
{
    private const string CreateAccount = "SourceFlow.Core.Tests.E2E.Commands.CreateAccount, SourceFlow.Core.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    private const string AccountUpdated = "SourceFlow.Core.Tests.E2E.Events.AccountUpdated, SourceFlow.Core.Tests, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    private const string CommandBody = """{"metadata":{"sequenceNo":1},"name":"CreateAccount","payload":{"id":7,"initialAmount":100,"accountName":"Ada"},"entity":{"id":7,"isNew":false}}""";

    [Fact]
    [Trait("Id", "UT-AWS-31")]
    [Trait("Covers", "doc 07 8")]
    public void TryRead_ReadsV2Commands_RawEvents_AndSnsNotifications()
    {
        AwsV2Bridge.TryRead(new Dictionary<string, string> { ["CommandType"] = CreateAccount, ["EntityId"] = "7", ["SequenceNo"] = "1" }, CommandBody, "sqs-1", out V2Message? command).ShouldBeTrue();
        command!.IsEvent.ShouldBeFalse();
        command.V2Type.ShouldBe(CreateAccount);
        command.EntityId.ShouldBe(7);
        command.SequenceNo.ShouldBe(1);
        command.MessageId.ShouldBe("sqs-1");
        command.Payload!["accountName"]!.GetValue<string>().ShouldBe("Ada");

        AwsV2Bridge.TryRead(new Dictionary<string, string> { ["EventType"] = AccountUpdated, ["EventName"] = "AccountUpdated" }, "{}", "sqs-2", out V2Message? raw).ShouldBeTrue();
        raw!.IsEvent.ShouldBeTrue();
        raw.EventName.ShouldBe("AccountUpdated");

        string notification = new JsonObject
        {
            ["Type"] = "Notification",
            ["MessageId"] = "sns-9",
            ["TopicArn"] = "arn:aws:sns:eu-west-1:000000000000:account-events",
            ["Message"] = """{"name":"AccountUpdated","payload":{"id":7,"balance":5}}""",
            ["MessageAttributes"] = new JsonObject
            {
                ["EventType"] = new JsonObject { ["Type"] = "String", ["Value"] = AccountUpdated },
                ["EventName"] = new JsonObject { ["Type"] = "String", ["Value"] = "AccountUpdated" },
            },
        }.ToJsonString();
        AwsV2Bridge.TryRead(new Dictionary<string, string>(), notification, "sqs-3", out V2Message? wrapped).ShouldBeTrue();
        wrapped!.IsEvent.ShouldBeTrue();
        wrapped.MessageId.ShouldBe("sns-9");      // the v2 id is SNS's, the same for every subscribed queue
        wrapped.Payload!["balance"]!.GetValue<int>().ShouldBe(5);

        AwsV2Bridge.TryRead(new Dictionary<string, string>(), "{\"Type\":\"Notification\"}", "sqs-4", out _).ShouldBeFalse();
        AwsV2Bridge.TryRead(new Dictionary<string, string>(), "not json", "sqs-5", out _).ShouldBeFalse();
        AwsV2Bridge.TryRead(new Dictionary<string, string> { ["ce_type"] = "x" }, "{}", "sqs-6", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-AWS-32")]
    [Trait("Covers", "doc 07 8")]
    public async Task Receiver_TranslatesMappedV2Messages_AndHandsUnmappedOnesOnForParking()
    {
        var options = new AwsTransportOptions().AcceptV2Messages(map => map.Type("SourceFlow.Core.Tests.E2E.Commands.CreateAccount, SourceFlow.Core.Tests", "bank.account.open", 1));
        var mocks = new AwsMocks(options, "orders");
        mocks.Deliver(
            V2Command("sqs-1", CreateAccount, CommandBody),
            V2Command("sqs-2", "Legacy.Commands.Unmapped, Legacy", "{}"),
            V2Command("sqs-3", CreateAccount, "not json"),
            new Message { MessageId = "sqs-4", ReceiptHandle = "rh-4", Body = "plain text", MessageAttributes = [] });
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []), CancellationToken.None);

        IReadOnlyList<ReceivedMessage> received = await receiver.ReceiveAsync(10, TimeSpan.Zero, CancellationToken.None);

        received.Count.ShouldBe(2);   // a mapped message whose body isn't JSON, and a non-v2 message, are left for the redrive policy
        MessageEnvelope mapped = received[0].Envelope;
        mapped.Type.ShouldBe("bank.account.open");
        mapped.MessageId.ShouldBe(V2MessageMap.MessageIdOf("sqs-1"));
        Encoding.UTF8.GetString(mapped.Body.Span).ShouldBe("""{"id":7,"initialAmount":100,"accountName":"Ada"}""");
        MessageEnvelope unmapped = received[1].Envelope;
        unmapped.Type.ShouldBe("Legacy.Commands.Unmapped, Legacy");
        unmapped.Headers[V2MessageMap.UnmappedHeader].ShouldBe("true");
    }

    [Fact]
    [Trait("Id", "UT-AWS-33")]
    [Trait("Covers", "doc 07 8")]
    public async Task Bridge_IsOffByDefault()
    {
        new AwsTransportOptions().V2Messages.ShouldBeNull();
        var mocks = new AwsMocks("orders");
        mocks.Deliver(V2Command("sqs-1", CreateAccount, CommandBody));
        await using ITransportReceiver receiver = await mocks.Transport.CreateReceiverAsync(new ReceiverOptions("orders", []), CancellationToken.None);

        (await receiver.ReceiveAsync(10, TimeSpan.Zero, CancellationToken.None)).ShouldBeEmpty();
    }

    private static Message V2Command(string id, string type, string body)
    {
        return new Message
        {
            MessageId = id,
            ReceiptHandle = "rh-" + id,
            Body = body,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["CommandType"] = new() { DataType = "String", StringValue = type },
                ["EntityId"] = new() { DataType = "String", StringValue = "7" },
                ["SequenceNo"] = new() { DataType = "String", StringValue = "1" },
            },
            Attributes = new Dictionary<string, string> { ["ApproximateReceiveCount"] = "1" },
        };
    }
}
