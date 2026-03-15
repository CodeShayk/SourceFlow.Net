using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.AWS.Messaging.Commands;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Messaging.Commands;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

[Trait("Category", "Unit")]
public class AwsSqsCommandListenerTests
{
    private static readonly string TestQueueUrl = "https://sqs.us-east-1.amazonaws.com/123456/test-queue.fifo";

    private readonly Mock<IAmazonSQS> _mockSqs;
    private readonly Mock<ICommandRoutingConfiguration> _mockRouting;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IServiceProvider> _mockScopedProvider;
    private readonly Mock<ICommandSubscriber> _mockSubscriber;
    private readonly AwsOptions _options;

    public AwsSqsCommandListenerTests()
    {
        _mockSqs = new Mock<IAmazonSQS>();
        _mockRouting = new Mock<ICommandRoutingConfiguration>();
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockScopedProvider = new Mock<IServiceProvider>();
        _mockSubscriber = new Mock<ICommandSubscriber>();
        _options = new AwsOptions { SqsMaxNumberOfMessages = 10, SqsReceiveWaitTimeSeconds = 0, SqsVisibilityTimeoutSeconds = 30 };

        // Wire up scoped service provider
        _mockServiceProvider
            .Setup(x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(_mockScopeFactory.Object);
        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(x => x.ServiceProvider).Returns(_mockScopedProvider.Object);
        _mockScopedProvider
            .Setup(x => x.GetService(typeof(ICommandSubscriber)))
            .Returns(_mockSubscriber.Object);

        _mockSubscriber
            .Setup(x => x.Subscribe(It.IsAny<TestCommand>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ExecuteAsync_NoQueuesConfigured_ReceiveMessageNeverCalled()
    {
        _mockRouting.Setup(x => x.GetListeningQueues()).Returns(Enumerable.Empty<string>());

        var listener = CreateListener();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await listener.StartAsync(cts.Token);
        await listener.StopAsync(CancellationToken.None);

        _mockSqs.Verify(
            x => x.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMessage_ValidCommand_CallsSubscriberAndDeletesMessage()
    {
        // Arrange
        _mockRouting.Setup(x => x.GetListeningQueues()).Returns(new[] { TestQueueUrl });

        var message = BuildValidCommandMessage();
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

        // Assert
        Assert.True(processed, "DeleteMessageAsync should have been called within 5 seconds");
        _mockSubscriber.Verify(x => x.Subscribe(It.IsAny<TestCommand>()), Times.Once);
        _mockSqs.Verify(
            x => x.DeleteMessageAsync(
                It.Is<DeleteMessageRequest>(r => r.ReceiptHandle == message.ReceiptHandle),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMessage_MissingCommandTypeAttribute_DeletesMessageForCleanup()
    {
        // Arrange
        _mockRouting.Setup(x => x.GetListeningQueues()).Returns(new[] { TestQueueUrl });

        var message = new Message
        {
            MessageId = "msg-no-attr",
            ReceiptHandle = "rh-no-attr",
            Body = "{}",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>() // missing CommandType
        };

        var deleted = new SemaphoreSlim(0, 1);
        SetupReceiveOnceAndBlock(message);
        _mockSqs
            .Setup(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => deleted.Release())
            .ReturnsAsync(new DeleteMessageResponse());

        var listener = CreateListener();
        await listener.StartAsync(CancellationToken.None);
        await Task.Delay(500); // give listener time to attempt processing
        await listener.StopAsync(CancellationToken.None);

        // Subscriber must NOT have been invoked
        _mockSubscriber.Verify(x => x.Subscribe(It.IsAny<TestCommand>()), Times.Never);
    }

    [Fact]
    public async Task ProcessMessage_UnresolvableCommandType_DoesNotCallSubscriber()
    {
        // Arrange
        _mockRouting.Setup(x => x.GetListeningQueues()).Returns(new[] { TestQueueUrl });

        var message = new Message
        {
            MessageId = "msg-bad-type",
            ReceiptHandle = "rh-bad-type",
            Body = "{}",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["CommandType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "NonExistent.Type.That.DoesNotExist, NoSuchAssembly"
                }
            }
        };

        SetupReceiveOnceAndBlock(message);
        _mockSqs
            .Setup(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteMessageResponse());

        var listener = CreateListener();
        await listener.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await listener.StopAsync(CancellationToken.None);

        _mockSubscriber.Verify(x => x.Subscribe(It.IsAny<TestCommand>()), Times.Never);
    }

    [Fact]
    public async Task ProcessMessage_InvalidJson_DeletesMessageAndDoesNotCallSubscriber()
    {
        // Arrange
        _mockRouting.Setup(x => x.GetListeningQueues()).Returns(new[] { TestQueueUrl });

        var message = new Message
        {
            MessageId = "msg-bad-json",
            ReceiptHandle = "rh-bad-json",
            Body = "not-valid-json{{{",
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["CommandType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = typeof(TestCommand).AssemblyQualifiedName
                }
            }
        };

        var deleted = new SemaphoreSlim(0, 1);
        SetupReceiveOnceAndBlock(message);
        _mockSqs
            .Setup(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => deleted.Release())
            .ReturnsAsync(new DeleteMessageResponse());

        var listener = CreateListener();
        await listener.StartAsync(CancellationToken.None);
        var cleaned = await deleted.WaitAsync(TimeSpan.FromSeconds(5));
        await listener.StopAsync(CancellationToken.None);

        Assert.True(cleaned, "Malformed message should be deleted to prevent infinite retries");
        _mockSubscriber.Verify(x => x.Subscribe(It.IsAny<TestCommand>()), Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Message BuildValidCommandMessage()
    {
        var command = new TestCommand();
        var json = JsonSerializer.Serialize(command, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        return new Message
        {
            MessageId = "msg-valid",
            ReceiptHandle = "rh-valid",
            Body = json,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["CommandType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = typeof(TestCommand).AssemblyQualifiedName
                }
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

    private AwsSqsCommandListener CreateListener() =>
        new AwsSqsCommandListener(
            _mockSqs.Object,
            _mockServiceProvider.Object,
            _mockRouting.Object,
            NullLogger<AwsSqsCommandListener>.Instance,
            _options);
}
