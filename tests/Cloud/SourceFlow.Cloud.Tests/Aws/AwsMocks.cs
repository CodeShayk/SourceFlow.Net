using System.Text;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Amazon.SQS.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SourceFlow.Cloud.Aws;
using SourceFlow.Messaging;

namespace SourceFlow.Cloud.Tests.Aws;

/// <summary>Substitute SQS and SNS clients for the transport's unit tests (no broker).</summary>
internal sealed class AwsMocks
{
    public AwsMocks(params string[] queues)
    {
        Sqs.GetQueueUrlAsync(Arg.Any<GetQueueUrlRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            string name = call.Arg<GetQueueUrlRequest>().QueueName;
            return queues.Contains(name)
                ? Task.FromResult(new GetQueueUrlResponse { QueueUrl = Url(name) })
                : Task.FromException<GetQueueUrlResponse>(new QueueDoesNotExistException($"{name} does not exist"));
        });
        Sqs.SendMessageBatchAsync(Arg.Any<SendMessageBatchRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            SendMessageBatchRequest request = call.Arg<SendMessageBatchRequest>();
            Batches.Add(request);
            return new SendMessageBatchResponse { Successful = [.. request.Entries.Select(e => new SendMessageBatchResultEntry { Id = e.Id })], Failed = [] };
        });
        Transport = new AwsTransport(Sqs, Sns, new AwsTransportOptions());
    }

    public IAmazonSQS Sqs { get; } = Substitute.For<IAmazonSQS>();

    public IAmazonSimpleNotificationService Sns { get; } = Substitute.For<IAmazonSimpleNotificationService>();

    public AwsTransport Transport { get; }

    public List<SendMessageBatchRequest> Batches { get; } = [];

    public static string Url(string queue)
    {
        return "https://sqs.eu-west-1.amazonaws.com/000000000000/" + queue;
    }

    public static MessageEnvelope Envelope(string? key = null, string body = "{\"n\":1}", string type = "orders.placed")
    {
        return new MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = type,
            TypeVersion = 1,
            Source = "orders",
            Time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PartitionKey = key,
            Body = Encoding.UTF8.GetBytes(body),
        };
    }

    /// <summary>Makes the next receive return these messages.</summary>
    public void Deliver(params Message[] messages)
    {
        Sqs.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new ReceiveMessageResponse { Messages = [.. messages] });
    }

    /// <summary>An SQS message carrying the envelope, as SQS returns it.</summary>
    public static Message Message(MessageEnvelope envelope, string? topic = null, int receiveCount = 1)
    {
        AwsOutgoingMessage wire = AwsWire.Encode(envelope, topic);
        return new Message
        {
            MessageId = Guid.CreateVersion7().ToString(),
            ReceiptHandle = "rh-" + envelope.MessageId,
            Body = wire.Body,
            MessageAttributes = wire.Attributes.ToDictionary(a => a.Key, a => new MessageAttributeValue { DataType = "String", StringValue = a.Value }),
            Attributes = new Dictionary<string, string> { ["ApproximateReceiveCount"] = receiveCount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };
    }

    public void LockLost()
    {
        Sqs.ChangeMessageVisibilityAsync(Arg.Any<ChangeMessageVisibilityRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ReceiptHandleIsInvalidException("The receipt handle has expired."));
        Sqs.DeleteMessageAsync(Arg.Any<DeleteMessageRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new ReceiptHandleIsInvalidException("The receipt handle has expired."));
    }
}
