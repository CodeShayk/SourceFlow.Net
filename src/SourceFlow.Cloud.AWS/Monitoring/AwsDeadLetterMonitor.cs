using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.DeadLetter;
using SourceFlow.Cloud.Observability;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Monitoring;

/// <summary>
/// Background service that monitors AWS SQS dead letter queues and processes dead lettered messages
/// </summary>
public class AwsDeadLetterMonitor : BackgroundService
{
    private readonly IAmazonSQS _sqsClient;
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly CloudMetrics _cloudMetrics;
    private readonly ILogger<AwsDeadLetterMonitor> _logger;
    private readonly AwsDeadLetterMonitorOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public AwsDeadLetterMonitor(
        IAmazonSQS sqsClient,
        IDeadLetterStore deadLetterStore,
        CloudMetrics cloudMetrics,
        ILogger<AwsDeadLetterMonitor> logger,
        AwsDeadLetterMonitorOptions options)
    {
        _sqsClient = sqsClient;
        _deadLetterStore = deadLetterStore;
        _cloudMetrics = cloudMetrics;
        _logger = logger;
        _options = options;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("AWS Dead Letter Monitor is disabled");
            return;
        }

        if (_options.DeadLetterQueues == null || !_options.DeadLetterQueues.Any())
        {
            _logger.LogWarning("No dead letter queues configured for monitoring");
            return;
        }

        _logger.LogInformation("Starting AWS Dead Letter Monitor for {QueueCount} queues",
            _options.DeadLetterQueues.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var queueUrl in _options.DeadLetterQueues)
                {
                    await MonitorQueue(queueUrl, stoppingToken);
                }

                // Wait for the configured interval before next check
                await Task.Delay(TimeSpan.FromSeconds(_options.CheckIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when shutting down
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in dead letter monitoring loop");
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); // Back off on error
            }
        }

        _logger.LogInformation("AWS Dead Letter Monitor stopped");
    }

    private async Task MonitorQueue(string queueUrl, CancellationToken cancellationToken)
    {
        try
        {
            // 1. Get queue depth
            var attributesRequest = new GetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                AttributeNames = new List<string>
                {
                    "ApproximateNumberOfMessages",
                    "ApproximateNumberOfMessagesNotVisible"
                }
            };

            var attributesResponse = await _sqsClient.GetQueueAttributesAsync(attributesRequest, cancellationToken);

            var messageCount = 0;
            if (attributesResponse.Attributes.TryGetValue("ApproximateNumberOfMessages", out var count))
            {
                int.TryParse(count, out messageCount);
            }

            // Update DLQ depth metric
            _cloudMetrics.UpdateDlqDepth(messageCount);

            if (messageCount == 0)
            {
                _logger.LogTrace("No messages in dead letter queue: {QueueUrl}", queueUrl);
                return;
            }

            _logger.LogInformation("Found {MessageCount} messages in dead letter queue: {QueueUrl}",
                messageCount, queueUrl);

            // 2. Receive messages from DLQ
            var receiveRequest = new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = Math.Min(_options.BatchSize, 10), // AWS max is 10
                WaitTimeSeconds = 0, // Short polling for DLQ monitoring
                MessageAttributeNames = new List<string> { "All" },
                AttributeNames = new List<string> { "All" },
                VisibilityTimeout = 30, // Short visibility timeout for monitoring
                MessageSystemAttributeNames = new List<string> { "All" },
                ReceiveRequestAttemptId = Guid.NewGuid().ToString() // Unique ID for this receive attempt
            };

            var receiveResponse = await _sqsClient.ReceiveMessageAsync(receiveRequest, cancellationToken);

            // 3. Process each dead letter message
            foreach (var message in receiveResponse.Messages)
            {
                await ProcessDeadLetter(message, queueUrl, messageCount, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring dead letter queue: {QueueUrl}", queueUrl);
        }
    }

    private async Task ProcessDeadLetter(Message message, string queueUrl, int queueDepth, CancellationToken cancellationToken)
    {
        try
        {
            // Extract receive count
            var receiveCount = 0;
            if (message.Attributes.TryGetValue("ApproximateReceiveCount", out var countStr))
            {
                int.TryParse(countStr, out receiveCount);
            }

            // Extract original queue URL (if available from redrive policy)
            var originalSource = "Unknown";
            if (message.MessageAttributes.TryGetValue("SourceQueue", out var sourceAttr))
            {
                originalSource = sourceAttr.StringValue ?? "Unknown";
            }

            // Extract message type
            var messageType = "Unknown";
            if (message.MessageAttributes.TryGetValue("CommandType", out var cmdTypeAttr))
            {
                messageType = cmdTypeAttr.StringValue ?? "Unknown";
            }
            else if (message.MessageAttributes.TryGetValue("EventType", out var evtTypeAttr))
            {
                messageType = evtTypeAttr.StringValue ?? "Unknown";
            }

            // Create dead letter record
            var record = new DeadLetterRecord
            {
                MessageId = message.MessageId,
                Body = message.Body,
                MessageType = messageType,
                Reason = "DeadLetterQueueThresholdExceeded",
                ErrorDescription = $"Message exceeded max receive count and was moved to DLQ. Receive count: {receiveCount}",
                OriginalSource = originalSource,
                DeadLetterSource = queueUrl,
                CloudProvider = "aws",
                DeadLetteredAt = DateTime.UtcNow,
                DeliveryCount = receiveCount,
                Metadata = new Dictionary<string, string>()
            };

            // Add all message attributes to metadata
            foreach (var attr in message.MessageAttributes)
            {
                record.Metadata[attr.Key] = attr.Value.StringValue ?? string.Empty;
            }

            // Add SQS attributes to metadata
            foreach (var attr in message.Attributes)
            {
                record.Metadata[$"Sqs.{attr.Key}"] = attr.Value;
            }

            // Save to store
            if (_options.StoreRecords)
            {
                await _deadLetterStore.SaveAsync(record, cancellationToken);
                _logger.LogInformation(
                    "Stored dead letter record: {MessageId}, Type: {MessageType}, DeliveryCount: {Count}",
                    record.MessageId, record.MessageType, record.DeliveryCount);
            }

            // Check if we should send alerts
            if (_options.SendAlerts && queueDepth >= _options.AlertThreshold)
            {
                _logger.LogWarning(
                    "ALERT: Dead letter queue threshold exceeded. Queue: {QueueUrl}, Count: {Count}, Threshold: {Threshold}",
                    queueUrl, queueDepth, _options.AlertThreshold);

                // TODO: Integrate with SNS for alerts
                // await _snsClient.PublishAsync(new PublishRequest { ... });
            }

            // Delete from DLQ if configured
            if (_options.DeleteAfterProcessing)
            {
                await _sqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle
                }, cancellationToken);

                _logger.LogDebug("Deleted message from DLQ: {MessageId}", message.MessageId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing dead letter message: {MessageId}", message.MessageId);
        }
    }

    /// <summary>
    /// Replay messages from DLQ back to the original queue
    /// </summary>
    public async Task<int> ReplayMessagesAsync(
        string deadLetterQueueUrl,
        string targetQueueUrl,
        int maxMessages = 10,
        CancellationToken cancellationToken = default)
    {
        var replayedCount = 0;

        try
        {
            _logger.LogInformation(
                "Starting message replay from DLQ {DlqUrl} to {TargetUrl}, MaxMessages: {MaxMessages}",
                deadLetterQueueUrl, targetQueueUrl, maxMessages);

            var receiveRequest = new ReceiveMessageRequest
            {
                QueueUrl = deadLetterQueueUrl,
                MaxNumberOfMessages = Math.Min(maxMessages, 10),
                WaitTimeSeconds = 0,
                MessageAttributeNames = new List<string> { "All" }
            };

            var receiveResponse = await _sqsClient.ReceiveMessageAsync(receiveRequest, cancellationToken);

            foreach (var message in receiveResponse.Messages)
            {
                // Send to target queue
                var sendRequest = new SendMessageRequest
                {
                    QueueUrl = targetQueueUrl,
                    MessageBody = message.Body,
                    MessageAttributes = message.MessageAttributes
                };

                await _sqsClient.SendMessageAsync(sendRequest, cancellationToken);

                // Delete from DLQ
                try
                {
                    await _sqsClient.DeleteMessageAsync(new DeleteMessageRequest
                    {
                        QueueUrl = deadLetterQueueUrl,
                        ReceiptHandle = message.ReceiptHandle
                    }, cancellationToken);
                }
                catch (Exception deleteEx)
                {
                    _logger.LogWarning(deleteEx,
                        "Message {MessageId} was replayed to {TargetQueue} but could not be deleted from DLQ {DlqUrl}. " +
                        "It may be replayed again. Manual cleanup may be required.",
                        message.MessageId, targetQueueUrl, deadLetterQueueUrl);
                }

                // Mark as replayed in store
                await _deadLetterStore.MarkAsReplayedAsync(message.MessageId, cancellationToken);

                replayedCount++;
                _logger.LogInformation("Replayed message {MessageId} from DLQ to {TargetQueue}",
                    message.MessageId, targetQueueUrl);
            }

            _logger.LogInformation("Message replay complete. Replayed {Count} messages", replayedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error replaying messages from DLQ");
            throw;
        }

        return replayedCount;
    }
}

/// <summary>
/// Configuration options for AWS Dead Letter Monitor
/// </summary>
public class AwsDeadLetterMonitorOptions
{
    /// <summary>
    /// Whether monitoring is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// List of dead letter queue URLs to monitor
    /// </summary>
    public List<string> DeadLetterQueues { get; set; } = new();

    /// <summary>
    /// How often to check DLQs (in seconds)
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of messages to process per batch
    /// </summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// Whether to store dead letter records
    /// </summary>
    public bool StoreRecords { get; set; } = true;

    /// <summary>
    /// Whether to send alerts
    /// </summary>
    public bool SendAlerts { get; set; } = true;

    /// <summary>
    /// Alert threshold (number of messages)
    /// </summary>
    public int AlertThreshold { get; set; } = 10;

    /// <summary>
    /// Whether to delete messages from DLQ after processing
    /// </summary>
    public bool DeleteAfterProcessing { get; set; } = false;
}
