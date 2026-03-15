using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.AWS.Messaging.Events;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Messaging.Events;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

[Trait("Category", "Unit")]
public class AwsSnsEventListenerTests
{
    private static readonly string TestQueueUrl = "https://sqs.us-east-1.amazonaws.com/123456/events-queue";

    private readonly Mock<IAmazonSQS> _mockSqs;
    private readonly Mock<IEventRoutingConfiguration> _mockRouting;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IServiceProvider> _mockScopedProvider;
    private readonly Mock<IEventSubscriber> _mockSubscriber;
    private readonly AwsOptions _options;

    public AwsSnsEventListenerTests()
    {
        _mockSqs = new Mock<IAmazonSQS>();
        _mockRouting = new Mock<IEventRoutingConfiguration>();
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockScopedProvider = new Mock<IServiceProvider>();
        _mockSubscriber = new Mock<IEventSubscriber>();
        _options = new AwsOptions { SqsMaxNumberOfMessages = 10, SqsReceiveWaitTimeSeconds = 0, SqsVisibilityTimeoutSeconds = 30 };

        _mockServiceProvider
            .Setup(x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(_mockScopeFactory.Object);
        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(x => x.ServiceProvider).Returns(_mockScopedProvider.Object);

        // GetServices<IEventSubscriber>() resolves IEnumerable<IEventSubscriber>
        _mockScopedProvider
            .Setup(x => x.GetService(typeof(IEnumerable<IEventSubscriber>)))
            .Returns(new[] { _mockSubscriber.Object });

        _mockSubscriber
            .Setup(x => x.Subscribe(It.IsAny<TestEvent>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ExecuteAsync_NoQueuesConfigured_ReceiveMessageNeverCalled()
    {
        _mockRouting.Setup(x => x.GetListeningQueues()).Returns(Enumerable.Empty<string>());

        var listener = CreateListener();
        await listener.StartAsync(CancellationToken.None);
        await listener.StopAsync(CancellationToken.None);

        _mockSqs.Verify(
            x => x.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMessage_ValidSnsNotification_CallsSubscriberAndDeletesMessage()
    {
        // Arrange
        _mockRouting.Setup(x => x.GetListeningQueues()).Returns(new[] { TestQueueUrl });

        var message = BuildValidSnsMessage("msg-valid");
        var deleted = new SemaphoreSlim(0, 1);

        SetupReceiveOnceAndBlock(message);
        _mockSqs
            .Setup(x => x.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => deleted.Release())
            .ReturnsAsync(new DeleteMessageResponse());

        var listener = CreateListener();
        await listener.StartAsync(CancellationToken.None);
        var processed = await deleted.WaitAsync(TimeSpan.FromSeconds(5));
        await listener.StopAsync(CancellationToken.None);

        Assert.True(processed, "Message should be deleted after successful event processing");
        _mockSubscriber.Verify(x => x.Subscribe(It.IsAny<TestEvent>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_MalformedJson_DeletesMalformedMessageToPreventRetries()
    {
        // Arrange
        _mockRouting.Setup(x => x.GetListeningQueues()).Returns(new[] { TestQueueUrl });

        var message = new Message
        {
            MessageId = "msg-bad-json",
            ReceiptHandle = "rh-bad-json",
            Body = "not-json{{{",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>()
        };

        var deleted = new SemaphoreSlim(0, 1);
        SetupReceiveOnceAndBlock(message);
        _mockSqs
            .Setup(x => x.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => deleted.Release())
            .ReturnsAsync(new DeleteMessageResponse());

        var listener = CreateListener();
        await listener.StartAsync(CancellationToken.None);
        var cleaned = await deleted.WaitAsync(TimeSpan.FromSeconds(5));
        await listener.StopAsync(CancellationToken.None);

        Assert.True(cleaned, "Malformed SNS notification should be deleted to prevent infinite retries");
        _mockSubscriber.Verify(x => x.Subscribe(It.IsAny<TestEvent>()), Times.Never);
    }

    [Fact]
    public async Task ProcessMessage_MissingEventTypeAttribute_SubscriberNotCalled()
    {
        // Arrange
        _mockRouting.Setup(x => x.GetListeningQueues()).Returns(new[] { TestQueueUrl });

        // SNS notification with no EventType attribute
        var snsBody = JsonSerializer.Serialize(new
        {
            Type = "Notification",
            MessageId = "sns-msg-id",
            TopicArn = "arn:aws:sns:us-east-1:123456:test-topic",
            Message = "{}",
            MessageAttributes = new Dictionary<string, object>() // empty — no EventType
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var message = new Message
        {
            MessageId = "msg-no-event-type",
            ReceiptHandle = "rh-no-event-type",
            Body = snsBody,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>()
        };

        SetupReceiveOnceAndBlock(message);

        var listener = CreateListener();
        await listener.StartAsync(CancellationToken.None);
        await Task.Delay(500); // give time to process
        await listener.StopAsync(CancellationToken.None);

        _mockSubscriber.Verify(x => x.Subscribe(It.IsAny<TestEvent>()), Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Message BuildValidSnsMessage(string messageId)
    {
        var @event = new TestEvent();
        var eventJson = JsonSerializer.Serialize(@event, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // SNS notification envelope (camelCase matches JsonNamingPolicy.CamelCase in listener)
        var snsBody = JsonSerializer.Serialize(new
        {
            type = "Notification",
            messageId = "sns-" + messageId,
            topicArn = "arn:aws:sns:us-east-1:123456:test-topic",
            subject = "",
            message = eventJson,
            messageAttributes = new Dictionary<string, object>
            {
                ["EventType"] = new { type = "String", value = typeof(TestEvent).AssemblyQualifiedName }
            }
        });

        return new Message
        {
            MessageId = messageId,
            ReceiptHandle = "rh-" + messageId,
            Body = snsBody,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>()
        };
    }

    private void SetupReceiveOnceAndBlock(Message message)
    {
        int callCount = 0;
        _mockSqs
            .Setup(x => x.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .Returns<ReceiveMessageRequest, CancellationToken>((_, ct) =>
                ++callCount == 1
                    ? Task.FromResult(new ReceiveMessageResponse { Messages = new List<Message> { message } })
                    : Task.Delay(Timeout.Infinite, ct).ContinueWith(
                        _ => new ReceiveMessageResponse(),
                        TaskContinuationOptions.OnlyOnCanceled));
    }

    private AwsSnsEventListener CreateListener() =>
        new AwsSnsEventListener(
            _mockSqs.Object,
            _mockServiceProvider.Object,
            _mockRouting.Object,
            NullLogger<AwsSnsEventListener>.Instance,
            _options);
}
