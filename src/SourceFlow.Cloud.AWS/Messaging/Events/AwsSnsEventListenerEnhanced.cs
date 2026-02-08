using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.AWS.Observability;
using SourceFlow.Cloud.Core.Configuration;
using SourceFlow.Cloud.Core.DeadLetter;
using SourceFlow.Cloud.Core.Observability;
using SourceFlow.Cloud.Core.Security;
using SourceFlow.Messaging.Events;
using SourceFlow.Observability;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Messaging.Events;

/// <summary>
/// Enhanced AWS SNS Event Listener with idempotency, tracing, metrics, and dead letter handling
/// </summary>
public class AwsSnsEventListenerEnhanced : BackgroundService
{
    private readonly IAmazonSQS _sqsClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAwsEventRoutingConfiguration _routingConfig;
    private readonly ILogger<AwsSnsEventListenerEnhanced> _logger;
    private readonly IDomainTelemetryService _domainTelemetry;
    private readonly CloudTelemetry _cloudTelemetry;
    private readonly CloudMetrics _cloudMetrics;
    private readonly IIdempotencyService _idempotencyService;
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly IMessageEncryption? _encryption;
    private readonly SensitiveDataMasker _dataMasker;
    private readonly AwsOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public AwsSnsEventListenerEnhanced(
        IAmazonSQS sqsClient,
        IServiceProvider serviceProvider,
        IAwsEventRoutingConfiguration routingConfig,
        ILogger<AwsSnsEventListenerEnhanced> logger,
        IDomainTelemetryService domainTelemetry,
        CloudTelemetry cloudTelemetry,
        CloudMetrics cloudMetrics,
        IIdempotencyService idempotencyService,
        IDeadLetterStore deadLetterStore,
        SensitiveDataMasker dataMasker,
        AwsOptions options,
        IMessageEncryption? encryption = null)
    {
        _sqsClient = sqsClient;
        _serviceProvider = serviceProvider;
        _routingConfig = routingConfig;
        _logger = logger;
        _domainTelemetry = domainTelemetry;
        _cloudTelemetry = cloudTelemetry;
        _cloudMetrics = cloudMetrics;
        _idempotencyService = idempotencyService;
        _deadLetterStore = deadLetterStore;
        _encryption = encryption;
        _dataMasker = dataMasker;
        _options = options;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Get all SQS queue URLs subscribed to SNS topics
        var queueUrls = _routingConfig.GetListeningQueues();

        if (!queueUrls.Any())
        {
            _logger.LogWarning("No SQS queues configured for SNS listening. AWS event listener will not start.");
            return;
        }

        var queueCount = queueUrls.Count();
        _logger.LogInformation("Starting AWS SNS event listener for {QueueCount} queues", queueCount);

        // Create listening tasks for each queue
        var listeningTasks = queueUrls.Select(queueUrl =>
            ListenToQueue(queueUrl, stoppingToken));

        await Task.WhenAll(listeningTasks);
    }

    private async Task ListenToQueue(string queueUrl, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting to listen to SQS queue for SNS events: {QueueUrl}", queueUrl);
        int retryCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var request = new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = _options.SqsMaxNumberOfMessages,
                    WaitTimeSeconds = _options.SqsReceiveWaitTimeSeconds,
                    MessageAttributeNames = new List<string> { "All" },
                    AttributeNames = new List<string> { "ApproximateReceiveCount" }
                };

                var response = await _sqsClient.ReceiveMessageAsync(request, cancellationToken);

                // Reset retry count on successful receive
                retryCount = 0;

                // Process each message (with parallel processing if configured)
                var processingTasks = response.Messages.Select(message =>
                    ProcessMessage(message, queueUrl, cancellationToken));

                await Task.WhenAll(processingTasks);

                // Record active processors
                _cloudMetrics.UpdateActiveProcessors(response.Messages.Count);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listening to SNS/SQS queue: {Queue}, Retry: {RetryCount}",
                    queueUrl, retryCount);

                // Exponential backoff with max delay of 60 seconds
                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retryCount), 60));
                retryCount++;

                await Task.Delay(delay, cancellationToken);
            }
        }

        _logger.LogInformation("Stopped listening to SNS/SQS queue: {QueueUrl}", queueUrl);
    }

    private async Task ProcessMessage(Message message, string queueUrl, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        string eventTypeName = "Unknown";
        Activity? activity = null;

        try
        {
            // 1. Parse SNS notification wrapper
            SnsNotification? snsNotification;
            try
            {
                snsNotification = JsonSerializer.Deserialize<SnsNotification>(message.Body, _jsonOptions);
                if (snsNotification == null)
                {
                    _logger.LogError("Failed to parse SNS notification (null result): {MessageId}", message.MessageId);
                    await CreateDeadLetterRecord(message, queueUrl, "NullSnsNotification",
                        "SNS notification deserialized to null");
                    return;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse SNS notification from message body: {MessageId}", message.MessageId);
                await CreateDeadLetterRecord(message, queueUrl, "SnsNotificationParseFailure",
                    ex.Message, ex);

                // Delete malformed message to prevent infinite retries
                await _sqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle
                }, cancellationToken);
                return;
            }

            // 2. Get event type from SNS message attributes
            eventTypeName = snsNotification.MessageAttributes?.GetValueOrDefault("EventType")?.Value ?? "Unknown";
            if (string.IsNullOrEmpty(eventTypeName))
            {
                _logger.LogError("SNS message missing EventType attribute: {MessageId}", message.MessageId);
                await CreateDeadLetterRecord(message, queueUrl, "MissingEventType",
                    "SNS message is missing the required EventType attribute");
                return;
            }

            var eventType = Type.GetType(eventTypeName);
            if (eventType == null)
            {
                _logger.LogError("Could not resolve event type: {EventType}", eventTypeName);
                await CreateDeadLetterRecord(message, queueUrl, "TypeResolutionFailure",
                    $"Could not resolve event type: {eventTypeName}");
                return;
            }

            // 3. Extract trace context from SNS message attributes
            var traceParent = snsNotification.MessageAttributes?.GetValueOrDefault("traceparent")?.Value;

            // 4. Extract sequence number for tracing
            long? sequenceNo = null;
            var seqNoValue = snsNotification.MessageAttributes?.GetValueOrDefault("SequenceNo")?.Value;
            if (!string.IsNullOrEmpty(seqNoValue) && long.TryParse(seqNoValue, out var seqValue))
                sequenceNo = seqValue;

            // 5. Start distributed trace activity
            activity = _cloudTelemetry.StartEventReceive(
                eventTypeName,
                queueUrl,
                "aws",
                traceParent,
                sequenceNo);

            // 6. Check idempotency before processing
            var idempotencyKey = $"{eventTypeName}:{message.MessageId}";
            var alreadyProcessed = await _idempotencyService.HasProcessedAsync(
                idempotencyKey,
                cancellationToken);

            if (alreadyProcessed)
            {
                sw.Stop();
                _logger.LogInformation(
                    "Duplicate event detected (idempotency): {EventType}, MessageId: {MessageId}, Duration: {Duration}ms",
                    eventTypeName, message.MessageId, sw.ElapsedMilliseconds);

                _cloudMetrics.RecordDuplicateDetected(eventTypeName, "aws");
                _cloudTelemetry.RecordSuccess(activity, sw.ElapsedMilliseconds);

                // Delete the duplicate message
                await _sqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle
                }, cancellationToken);

                return;
            }

            // 7. Decrypt message body if encryption is enabled
            var messageBody = snsNotification.Message;
            if (_encryption != null)
            {
                messageBody = await _encryption.DecryptAsync(messageBody);
                _logger.LogDebug("Event message decrypted using {Algorithm}",
                    _encryption.AlgorithmName);
            }

            // 8. Record message size
            _cloudMetrics.RecordMessageSize(messageBody.Length, eventTypeName, "aws");

            // 9. Deserialize event from SNS message body
            var @event = JsonSerializer.Deserialize(messageBody, eventType, _jsonOptions) as IEvent;
            if (@event == null)
            {
                _logger.LogError("Failed to deserialize event: {EventType}", eventTypeName);
                await CreateDeadLetterRecord(message, queueUrl, "DeserializationFailure",
                    $"Failed to deserialize event of type: {eventTypeName}");
                return;
            }

            // 10. Get event subscribers and invoke Subscribe method
            using var scope = _serviceProvider.CreateScope();
            var eventSubscribers = scope.ServiceProvider.GetServices<IEventSubscriber>();

            var subscribeMethod = typeof(IEventSubscriber)
                .GetMethod("Subscribe")
                ?.MakeGenericMethod(eventType);

            if (subscribeMethod == null)
            {
                _logger.LogError("Could not find Subscribe method for event type: {EventType}", eventTypeName);
                await CreateDeadLetterRecord(message, queueUrl, "SubscriptionFailure",
                    $"Could not find Subscribe method for: {eventTypeName}");
                return;
            }

            // 11. Process the event with all subscribers
            var tasks = eventSubscribers.Select(subscriber =>
            {
                try
                {
                    return (Task)subscribeMethod.Invoke(subscriber, new[] { @event })!;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error invoking Subscribe method for event type: {EventType}", eventTypeName);
                    return Task.CompletedTask;
                }
            });

            await Task.WhenAll(tasks);

            // 12. Mark as processed in idempotency service
            await _idempotencyService.MarkAsProcessedAsync(
                idempotencyKey,
                TimeSpan.FromHours(24),
                cancellationToken);

            // 13. Delete message from queue (successful processing)
            await _sqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = queueUrl,
                ReceiptHandle = message.ReceiptHandle
            }, cancellationToken);

            // 14. Record success metrics
            sw.Stop();
            _cloudTelemetry.RecordSuccess(activity, sw.ElapsedMilliseconds);
            _cloudMetrics.RecordEventReceived(eventTypeName, queueUrl, "aws");

            // 15. Log with masked sensitive data
            _logger.LogInformation(
                "Event processed from SNS: {EventType} -> {Queue}, Duration: {Duration}ms, MessageId: {MessageId}, Event: {Event}",
                eventTypeName, queueUrl, sw.ElapsedMilliseconds, message.MessageId,
                _dataMasker.Mask(@event));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _cloudTelemetry.RecordError(activity, ex, sw.ElapsedMilliseconds);

            _logger.LogError(ex,
                "Error processing SNS message: {EventType}, MessageId: {MessageId}, Duration: {Duration}ms",
                eventTypeName, message.MessageId, sw.ElapsedMilliseconds);

            // Create dead letter record for persistent failures
            var receiveCount = GetReceiveCount(message);
            if (receiveCount > 3) // Threshold for moving to DLQ
            {
                await CreateDeadLetterRecord(message, queueUrl, "ProcessingFailure",
                    ex.Message, ex);
            }

            // Message will return to queue after visibility timeout
            // or move to DLQ if maxReceiveCount is exceeded
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private int GetReceiveCount(Message message)
    {
        if (message.Attributes.TryGetValue("ApproximateReceiveCount", out var countStr) &&
            int.TryParse(countStr, out var count))
        {
            return count;
        }
        return 0;
    }

    private async Task CreateDeadLetterRecord(
        Message message,
        string queueUrl,
        string reason,
        string errorDescription,
        Exception? exception = null)
    {
        try
        {
            var receiveCount = GetReceiveCount(message);

            var record = new DeadLetterRecord
            {
                MessageId = message.MessageId,
                Body = message.Body,
                MessageType = "SNS Event (type extraction failed)",
                Reason = reason,
                ErrorDescription = errorDescription,
                OriginalSource = queueUrl,
                DeadLetterSource = $"{queueUrl}-dlq",
                CloudProvider = "aws",
                DeadLetteredAt = DateTime.UtcNow,
                DeliveryCount = receiveCount,
                ExceptionType = exception?.GetType().FullName,
                ExceptionMessage = exception?.Message,
                ExceptionStackTrace = exception?.StackTrace,
                Metadata = new Dictionary<string, string>()
            };

            // Try to extract event type from SNS message if possible
            try
            {
                var snsNotification = JsonSerializer.Deserialize<SnsNotification>(message.Body, _jsonOptions);
                if (snsNotification?.MessageAttributes != null)
                {
                    var eventType = snsNotification.MessageAttributes.GetValueOrDefault("EventType")?.Value;
                    if (!string.IsNullOrEmpty(eventType))
                    {
                        record.MessageType = eventType;
                    }

                    foreach (var attr in snsNotification.MessageAttributes)
                    {
                        record.Metadata[attr.Key] = attr.Value?.Value ?? string.Empty;
                    }
                }
            }
            catch
            {
                // Ignore errors during metadata extraction for DLR
            }

            await _deadLetterStore.SaveAsync(record);

            _logger.LogWarning(
                "Dead letter record created: {MessageId}, Type: {MessageType}, Reason: {Reason}, DeliveryCount: {Count}",
                record.MessageId, record.MessageType, record.Reason, record.DeliveryCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create dead letter record for message: {MessageId}",
                message.MessageId);
        }
    }

    // SNS notification wrapper structure
    private class SnsNotification
    {
        public string Type { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public string TopicArn { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, SnsMessageAttribute>? MessageAttributes { get; set; }
    }

    private class SnsMessageAttribute
    {
        public string Type { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}

// Extension method to safely get dictionary values
file static class DictionaryExtensions
{
    public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue>? dictionary, TKey key)
    {
        if (dictionary == null) return default;
        return dictionary.TryGetValue(key, out var value) ? value : default;
    }
}
