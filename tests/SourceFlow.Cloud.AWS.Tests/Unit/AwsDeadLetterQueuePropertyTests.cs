using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SourceFlow.Cloud.AWS.Monitoring;
using SourceFlow.Cloud.DeadLetter;
using SourceFlow.Cloud.Observability;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AwsDeadLetterMonitor"/>.
/// </summary>
[Trait("Category", "Unit")]
public class AwsDeadLetterMonitorTests
{
    private readonly Mock<IAmazonSQS> _mockSqsClient;
    private readonly Mock<IDeadLetterStore> _mockDeadLetterStore;
    private readonly CloudMetrics _cloudMetrics;

    private const string DlqUrl = "https://sqs.us-east-1.amazonaws.com/123456/test-dlq";
    private const string TargetQueueUrl = "https://sqs.us-east-1.amazonaws.com/123456/test-queue";

    public AwsDeadLetterMonitorTests()
    {
        _mockSqsClient = new Mock<IAmazonSQS>();
        _mockDeadLetterStore = new Mock<IDeadLetterStore>();
        _cloudMetrics = new CloudMetrics(NullLogger<CloudMetrics>.Instance);
    }

    // ── ReplayMessagesAsync tests (public method, testable directly) ──────────

    [Fact]
    public async Task ReplayMessagesAsync_MessagesInDlq_SendsToTargetQueue()
    {
        // Arrange
        var messageId = Guid.NewGuid().ToString();
        var receiptHandle = "receipt-handle-1";

        _mockSqsClient
            .Setup(x => x.ReceiveMessageAsync(
                It.Is<ReceiveMessageRequest>(r => r.QueueUrl == DlqUrl),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse
            {
                Messages = new List<Message>
                {
                    new Message
                    {
                        MessageId = messageId,
                        Body = "{\"test\":\"value\"}",
                        ReceiptHandle = receiptHandle,
                        MessageAttributes = new Dictionary<string, MessageAttributeValue>()
                    }
                }
            });

        _mockSqsClient
            .Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse { MessageId = Guid.NewGuid().ToString() });

        _mockSqsClient
            .Setup(x => x.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteMessageResponse());

        _mockDeadLetterStore
            .Setup(x => x.MarkAsReplayedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var monitor = CreateMonitor(new AwsDeadLetterMonitorOptions
        {
            Enabled = true,
            DeadLetterQueues = new List<string> { DlqUrl }
        });

        // Act
        var replayedCount = await monitor.ReplayMessagesAsync(DlqUrl, TargetQueueUrl, maxMessages: 10);

        // Assert
        Assert.Equal(1, replayedCount);
        _mockSqsClient.Verify(
            x => x.SendMessageAsync(
                It.Is<SendMessageRequest>(r => r.QueueUrl == TargetQueueUrl),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReplayMessagesAsync_MessageSentToTarget_DeletesFromDlq()
    {
        // Arrange
        var receiptHandle = "receipt-handle-delete-test";

        _mockSqsClient
            .Setup(x => x.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse
            {
                Messages = new List<Message>
                {
                    new Message
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Body = "body",
                        ReceiptHandle = receiptHandle,
                        MessageAttributes = new Dictionary<string, MessageAttributeValue>()
                    }
                }
            });

        _mockSqsClient
            .Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse { MessageId = Guid.NewGuid().ToString() });

        _mockSqsClient
            .Setup(x => x.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteMessageResponse());

        _mockDeadLetterStore
            .Setup(x => x.MarkAsReplayedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var monitor = CreateMonitor(new AwsDeadLetterMonitorOptions
        {
            Enabled = true,
            DeadLetterQueues = new List<string> { DlqUrl }
        });

        // Act
        await monitor.ReplayMessagesAsync(DlqUrl, TargetQueueUrl);

        // Assert: delete was called on the DLQ for this receipt handle
        _mockSqsClient.Verify(
            x => x.DeleteMessageAsync(
                It.Is<DeleteMessageRequest>(r =>
                    r.QueueUrl == DlqUrl &&
                    r.ReceiptHandle == receiptHandle),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReplayMessagesAsync_MessageReplayed_MarkAsReplayedCalledOnStore()
    {
        // Arrange
        var messageId = "msg-replay-id";

        _mockSqsClient
            .Setup(x => x.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse
            {
                Messages = new List<Message>
                {
                    new Message
                    {
                        MessageId = messageId,
                        Body = "body",
                        ReceiptHandle = "rh",
                        MessageAttributes = new Dictionary<string, MessageAttributeValue>()
                    }
                }
            });

        _mockSqsClient
            .Setup(x => x.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse { MessageId = Guid.NewGuid().ToString() });

        _mockSqsClient
            .Setup(x => x.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteMessageResponse());

        _mockDeadLetterStore
            .Setup(x => x.MarkAsReplayedAsync(messageId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var monitor = CreateMonitor(new AwsDeadLetterMonitorOptions
        {
            Enabled = true,
            DeadLetterQueues = new List<string> { DlqUrl }
        });

        // Act
        await monitor.ReplayMessagesAsync(DlqUrl, TargetQueueUrl);

        // Assert
        _mockDeadLetterStore.Verify(
            x => x.MarkAsReplayedAsync(messageId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReplayMessagesAsync_EmptyDlq_ReturnsZero()
    {
        // Arrange
        _mockSqsClient
            .Setup(x => x.ReceiveMessageAsync(It.IsAny<ReceiveMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReceiveMessageResponse { Messages = new List<Message>() });

        var monitor = CreateMonitor(new AwsDeadLetterMonitorOptions
        {
            Enabled = true,
            DeadLetterQueues = new List<string> { DlqUrl }
        });

        // Act
        var replayedCount = await monitor.ReplayMessagesAsync(DlqUrl, TargetQueueUrl);

        // Assert
        Assert.Equal(0, replayedCount);
    }

    // ── ExecuteAsync path: delete-after-processing tests ─────────────────────

    [Fact]
    public async Task ExecuteAsync_DeleteAfterProcessingTrue_DeleteMessageCalledAfterSave()
    {
        // Arrange
        var receiptHandle = "rh-delete-after";
        var messageId = "msg-delete-after";

        SetupMonitorQueueAttributes(1);
        SetupReceiveMessages(new List<Message>
        {
            new Message
            {
                MessageId = messageId,
                Body = "{\"test\":1}",
                ReceiptHandle = receiptHandle,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>(),
                Attributes = new Dictionary<string, string>
                {
                    ["ApproximateReceiveCount"] = "4"
                }
            }
        });

        _mockSqsClient
            .Setup(x => x.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteMessageResponse());

        _mockDeadLetterStore
            .Setup(x => x.SaveAsync(It.IsAny<DeadLetterRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cts = new CancellationTokenSource();

        var monitor = CreateMonitor(new AwsDeadLetterMonitorOptions
        {
            Enabled = true,
            DeadLetterQueues = new List<string> { DlqUrl },
            CheckIntervalSeconds = 0,
            StoreRecords = true,
            DeleteAfterProcessing = true
        });

        // Act: start, allow one iteration, then cancel
        var task = monitor.StartAsync(cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();

        try { await task; } catch (OperationCanceledException) { }

        // Assert: both save and delete were called
        _mockDeadLetterStore.Verify(
            x => x.SaveAsync(It.IsAny<DeadLetterRecord>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        _mockSqsClient.Verify(
            x => x.DeleteMessageAsync(
                It.Is<DeleteMessageRequest>(r => r.ReceiptHandle == receiptHandle),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_DeleteAfterProcessingFalse_DeleteMessageNeverCalled()
    {
        // Arrange
        SetupMonitorQueueAttributes(1);
        SetupReceiveMessages(new List<Message>
        {
            new Message
            {
                MessageId = "msg-no-delete",
                Body = "{\"test\":1}",
                ReceiptHandle = "rh-no-delete",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>(),
                Attributes = new Dictionary<string, string>
                {
                    ["ApproximateReceiveCount"] = "2"
                }
            }
        });

        _mockDeadLetterStore
            .Setup(x => x.SaveAsync(It.IsAny<DeadLetterRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cts = new CancellationTokenSource();

        var monitor = CreateMonitor(new AwsDeadLetterMonitorOptions
        {
            Enabled = true,
            DeadLetterQueues = new List<string> { DlqUrl },
            CheckIntervalSeconds = 0,
            StoreRecords = true,
            DeleteAfterProcessing = false
        });

        // Act
        var task = monitor.StartAsync(cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();

        try { await task; } catch (OperationCanceledException) { }

        // Assert: delete should NOT have been called
        _mockSqsClient.Verify(
            x => x.DeleteMessageAsync(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_StoreRecordsTrue_SaveAsyncCalled()
    {
        // Arrange
        SetupMonitorQueueAttributes(1);
        SetupReceiveMessages(new List<Message>
        {
            new Message
            {
                MessageId = "msg-store",
                Body = "{\"data\":1}",
                ReceiptHandle = "rh-store",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>(),
                Attributes = new Dictionary<string, string>
                {
                    ["ApproximateReceiveCount"] = "3"
                }
            }
        });

        _mockDeadLetterStore
            .Setup(x => x.SaveAsync(It.IsAny<DeadLetterRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cts = new CancellationTokenSource();

        var monitor = CreateMonitor(new AwsDeadLetterMonitorOptions
        {
            Enabled = true,
            DeadLetterQueues = new List<string> { DlqUrl },
            CheckIntervalSeconds = 0,
            StoreRecords = true,
            DeleteAfterProcessing = false
        });

        // Act
        var task = monitor.StartAsync(cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();

        try { await task; } catch (OperationCanceledException) { }

        // Assert
        _mockDeadLetterStore.Verify(
            x => x.SaveAsync(It.IsAny<DeadLetterRecord>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_StoreRecordsFalse_SaveAsyncNeverCalled()
    {
        // Arrange
        SetupMonitorQueueAttributes(1);
        SetupReceiveMessages(new List<Message>
        {
            new Message
            {
                MessageId = "msg-no-store",
                Body = "{}",
                ReceiptHandle = "rh-no-store",
                MessageAttributes = new Dictionary<string, MessageAttributeValue>(),
                Attributes = new Dictionary<string, string>()
            }
        });

        var cts = new CancellationTokenSource();

        var monitor = CreateMonitor(new AwsDeadLetterMonitorOptions
        {
            Enabled = true,
            DeadLetterQueues = new List<string> { DlqUrl },
            CheckIntervalSeconds = 0,
            StoreRecords = false,
            DeleteAfterProcessing = false
        });

        // Act
        var task = monitor.StartAsync(cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();

        try { await task; } catch (OperationCanceledException) { }

        // Assert
        _mockDeadLetterStore.Verify(
            x => x.SaveAsync(It.IsAny<DeadLetterRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Disabled_QueuesNotPolled()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        var monitor = CreateMonitor(new AwsDeadLetterMonitorOptions
        {
            Enabled = false,
            DeadLetterQueues = new List<string> { DlqUrl }
        });

        // Act
        await monitor.StartAsync(cts.Token);
        await cts.CancelAsync();

        // Assert: SQS was never called because monitoring is disabled
        _mockSqsClient.Verify(
            x => x.GetQueueAttributesAsync(It.IsAny<GetQueueAttributesRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupMonitorQueueAttributes(int messageCount)
    {
        _mockSqsClient
            .Setup(x => x.GetQueueAttributesAsync(
                It.Is<GetQueueAttributesRequest>(r => r.QueueUrl == DlqUrl),
                It.IsAny<CancellationToken>()))
            .Returns<GetQueueAttributesRequest, CancellationToken>(async (_, ct) =>
            {
                // Task.Yield() forces a yield to the scheduler so the test thread can run,
                // preventing the tight loop from blocking StartAsync forever.
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                return new GetQueueAttributesResponse
                {
                    Attributes = new Dictionary<string, string>
                    {
                        ["ApproximateNumberOfMessages"] = messageCount.ToString()
                    }
                };
            });
    }

    private void SetupReceiveMessages(List<Message> messages)
    {
        _mockSqsClient
            .Setup(x => x.ReceiveMessageAsync(
                It.Is<ReceiveMessageRequest>(r => r.QueueUrl == DlqUrl),
                It.IsAny<CancellationToken>()))
            .Returns<ReceiveMessageRequest, CancellationToken>(async (_, ct) =>
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                return new ReceiveMessageResponse { Messages = messages };
            });
    }

    private AwsDeadLetterMonitor CreateMonitor(AwsDeadLetterMonitorOptions options)
    {
        return new AwsDeadLetterMonitor(
            _mockSqsClient.Object,
            _mockDeadLetterStore.Object,
            _cloudMetrics,
            NullLogger<AwsDeadLetterMonitor>.Instance,
            options);
    }
}
