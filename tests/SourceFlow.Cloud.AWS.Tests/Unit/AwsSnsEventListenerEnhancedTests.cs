using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.AWS.Messaging.Events;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.DeadLetter;
using SourceFlow.Cloud.Observability;
using SourceFlow.Cloud.Security;
using SourceFlow.Messaging.Events;
using SourceFlow.Observability;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

[Trait("Category", "Unit")]
public class AwsSnsEventListenerEnhancedTests
{
    private static readonly string TestQueueUrl = "https://sqs.us-east-1.amazonaws.com/123456/events-queue";

    private readonly Mock<IAmazonSQS> _mockSqs;
    private readonly Mock<IEventRoutingConfiguration> _mockRouting;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IServiceProvider> _mockScopedProvider;
    private readonly Mock<IEventSubscriber> _mockSubscriber;
    private readonly Mock<IDomainTelemetryService> _mockDomainTelemetry;
    private readonly Mock<IIdempotencyService> _mockIdempotency;
    private readonly Mock<IDeadLetterStore> _mockDeadLetterStore;
    private readonly CloudTelemetry _cloudTelemetry;
    private readonly CloudMetrics _cloudMetrics;
    private readonly SensitiveDataMasker _dataMasker;
    private readonly AwsOptions _options;

    public AwsSnsEventListenerEnhancedTests()
    {
        _mockSqs = new Mock<IAmazonSQS>();
        _mockRouting = new Mock<IEventRoutingConfiguration>();
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockScopedProvider = new Mock<IServiceProvider>();
        _mockSubscriber = new Mock<IEventSubscriber>();
        _mockDomainTelemetry = new Mock<IDomainTelemetryService>();
        _mockIdempotency = new Mock<IIdempotencyService>();
        _mockDeadLetterStore = new Mock<IDeadLetterStore>();
        _cloudTelemetry = new CloudTelemetry(NullLogger<CloudTelemetry>.Instance);
        _cloudMetrics = new CloudMetrics(NullLogger<CloudMetrics>.Instance);
        _dataMasker = new SensitiveDataMasker();
        _options = new AwsOptions { SqsMaxNumberOfMessages = 10, SqsReceiveWaitTimeSeconds = 0, SqsVisibilityTimeoutSeconds = 30 };

        _mockServiceProvider
            .Setup(x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(_mockScopeFactory.Object);
        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(x => x.ServiceProvider).Returns(_mockScopedProvider.Object);
        _mockScopedProvider
            .Setup(x => x.GetService(typeof(IEnumerable<IEventSubscriber>)))
            .Returns(new[] { _mockSubscriber.Object });

        _mockSubscriber
            .Setup(x => x.Subscribe(It.IsAny<TestEvent>()))
            .Returns(Task.CompletedTask);

        _mockDeadLetterStore
            .Setup(x => x.SaveAsync(It.IsAny<DeadLetterRecord>()))
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
    public async Task ProcessMessage_DuplicateEvent_SubscriberNotCalledMessageDeleted()
    {
        // Arrange — idempotency: already processed
        _mockRouting.Setup(x => x.GetListeningQueues()).Returns(new[] { TestQueueUrl });
        _mockIdempotency
            .Setup(x => x.HasProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var message = BuildValidSnsMessage("msg-dup");
        var deleted = new SemaphoreSlim(0, 1);

        SetupReceiveOnceAndBlock(message);
        _mockSqs
            .Setup(x => x.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => deleted.Release())
            .ReturnsAsync(new DeleteMessageResponse());

        var listener = CreateListener();
        await listener.StartAsync(CancellationToken.None);
        var messageDeleted = await deleted.WaitAsync(TimeSpan.FromSeconds(5));
        await listener.StopAsync(CancellationToken.None);

        Assert.True(messageDeleted, "Duplicate event message should be deleted");
        _mockSubscriber.Verify(x => x.Subscribe(It.IsAny<TestEvent>()), Times.Never);
        _mockIdempotency.Verify(
            x => x.MarkAsProcessedAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMessage_ValidEvent_SubscriberCalledAndMarkedProcessed()
    {
        // Arrange
        _mockRouting.Setup(x => x.GetListeningQueues()).Returns(new[] { TestQueueUrl });
        _mockIdempotency
            .Setup(x => x.HasProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _mockIdempotency
            .Setup(x => x.MarkAsProcessedAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

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
        _mockIdempotency.Verify(
            x => x.MarkAsProcessedAsync(
                It.Is<string>(k => k.Contains(typeof(TestEvent).FullName!)),
                TimeSpan.FromHours(24),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_EncryptionEnabled_DecryptCalledBeforeDeserialization()
    {
        // Arrange
        _mockRouting.Setup(x => x.GetListeningQueues()).Returns(new[] { TestQueueUrl });
        _mockIdempotency
            .Setup(x => x.HasProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _mockIdempotency
            .Setup(x => x.MarkAsProcessedAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockEncryption = new Mock<IMessageEncryption>();
        mockEncryption.Setup(x => x.AlgorithmName).Returns("TEST");
        mockEncryption
            .Setup(x => x.DecryptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((s, _) => Task.FromResult(s)); // identity decryption

        var message = BuildValidSnsMessage("msg-enc");
        var deleted = new SemaphoreSlim(0, 1);

        SetupReceiveOnceAndBlock(message);
        _mockSqs
            .Setup(x => x.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => deleted.Release())
            .ReturnsAsync(new DeleteMessageResponse());

        var listener = CreateListener(encryption: mockEncryption.Object);
        await listener.StartAsync(CancellationToken.None);
        await deleted.WaitAsync(TimeSpan.FromSeconds(5));
        await listener.StopAsync(CancellationToken.None);

        mockEncryption.Verify(x => x.DecryptAsync(It.IsAny<string>()), Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Message BuildValidSnsMessage(string messageId)
    {
        var @event = new TestEvent();
        var eventJson = JsonSerializer.Serialize(@event, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

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
            MessageAttributes = new Dictionary<string, MessageAttributeValue>(),
            Attributes = new Dictionary<string, string>
            {
                ["ApproximateReceiveCount"] = "1"
            }
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

    private AwsSnsEventListenerEnhanced CreateListener(IMessageEncryption? encryption = null) =>
        new AwsSnsEventListenerEnhanced(
            _mockSqs.Object,
            _mockServiceProvider.Object,
            _mockRouting.Object,
            NullLogger<AwsSnsEventListenerEnhanced>.Instance,
            _mockDomainTelemetry.Object,
            _cloudTelemetry,
            _cloudMetrics,
            _mockIdempotency.Object,
            _mockDeadLetterStore.Object,
            _dataMasker,
            _options,
            encryption);
}
