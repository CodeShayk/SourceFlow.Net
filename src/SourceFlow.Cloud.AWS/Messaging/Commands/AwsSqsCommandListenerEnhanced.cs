using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.AWS.Observability;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.DeadLetter;
using SourceFlow.Cloud.Observability;
using SourceFlow.Cloud.Security;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Messaging.Commands;

/// <summary>
/// Enhanced AWS SQS Command Listener with idempotency, tracing, metrics, and dead letter handling
/// </summary>
public class AwsSqsCommandListenerEnhanced : BackgroundService
{
    private static readonly ConcurrentDictionary<string, Type?> _typeCache = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo?> _methodInfoCache = new();

    private readonly IAmazonSQS _sqsClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly ICommandRoutingConfiguration _routingConfig;
    private readonly ILogger<AwsSqsCommandListenerEnhanced> _logger;
    private readonly IDomainTelemetryService _domainTelemetry;
    private readonly CloudTelemetry _cloudTelemetry;
    private readonly CloudMetrics _cloudMetrics;
    private readonly IIdempotencyService _idempotencyService;
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly IMessageEncryption? _encryption;
    private readonly SensitiveDataMasker _dataMasker;
    private readonly AwsOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public AwsSqsCommandListenerEnhanced(
        IAmazonSQS sqsClient,
        IServiceProvider serviceProvider,
        ICommandRoutingConfiguration routingConfig,
        ILogger<AwsSqsCommandListenerEnhanced> logger,
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
        // Get all queue URLs to listen to
        var queueUrls = _routingConfig.GetListeningQueues();

        if (!queueUrls.Any())
        {
            _logger.LogWarning("No SQS queues configured for listening. AWS command listener will not start.");
            return;
        }

        var queueCount = queueUrls.Count();
        _logger.LogInformation("Starting AWS SQS command listener for {QueueCount} queues", queueCount);

        // Create listening tasks for each queue
        var listeningTasks = queueUrls.Select(queueUrl =>
            ListenToQueue(queueUrl, stoppingToken));

        await Task.WhenAll(listeningTasks);
    }

    private async Task ListenToQueue(string queueUrl, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting to listen to SQS queue: {QueueUrl}", queueUrl);
        int retryCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 1. Long-poll SQS (up to 20 seconds)
                var request = new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = _options.SqsMaxNumberOfMessages,
                    WaitTimeSeconds = _options.SqsReceiveWaitTimeSeconds,
                    MessageAttributeNames = new List<string> { "All" },
                    AttributeNames = new List<string> { "ApproximateReceiveCount" },
                    VisibilityTimeout = _options.SqsVisibilityTimeoutSeconds,
                    MessageSystemAttributeNames = new List<string> { "All" },
                    ReceiveRequestAttemptId = Guid.NewGuid().ToString() // For FIFO queues to ensure exactly-once processing
                };

                var response = await _sqsClient.ReceiveMessageAsync(request, cancellationToken);

                // Reset retry count on successful receive
                retryCount = 0;

                // 2. Process each message (with parallel processing if configured)
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
                _logger.LogError(ex, "Error listening to SQS queue: {Queue}, Retry: {RetryCount}",
                    queueUrl, retryCount);

                // Exponential backoff with max delay of 60 seconds
                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retryCount), 60));
                retryCount++;

                await Task.Delay(delay, cancellationToken);
            }
        }

        _logger.LogInformation("Stopped listening to SQS queue: {QueueUrl}", queueUrl);
    }

    private async Task ProcessMessage(Message message, string queueUrl, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        string commandTypeName = "Unknown";
        Activity? activity = null;

        try
        {
            // 1. Get command type from message attributes
            if (!message.MessageAttributes.TryGetValue("CommandType", out var commandTypeAttribute))
            {
                _logger.LogError("Message missing CommandType attribute: {MessageId}", message.MessageId);
                await CreateDeadLetterRecord(message, queueUrl, "MissingCommandType",
                    "Message is missing the required CommandType attribute");
                return;
            }

            commandTypeName = commandTypeAttribute.StringValue;
            var commandType = _typeCache.GetOrAdd(commandTypeName, static name => Type.GetType(name));

            if (commandType == null)
            {
                _logger.LogError("Could not resolve command type: {CommandType}", commandTypeName);
                await CreateDeadLetterRecord(message, queueUrl, "TypeResolutionFailure",
                    $"Could not resolve command type: {commandTypeName}");
                return;
            }

            // 2. Extract trace context
            var traceParent = ExtractTraceParent(message.MessageAttributes);

            // 3. Extract entity ID and sequence number for tracing
            object? entityId = null;
            long? sequenceNo = null;

            if (message.MessageAttributes.TryGetValue("EntityId", out var entityIdAttr))
                entityId = entityIdAttr.StringValue;

            if (message.MessageAttributes.TryGetValue("SequenceNo", out var seqAttr) &&
                long.TryParse(seqAttr.StringValue, out var seqValue))
                sequenceNo = seqValue;

            // 4. Start distributed trace activity
            activity = _cloudTelemetry.StartCommandProcess(
                commandTypeName,
                queueUrl,
                "aws",
                traceParent,
                entityId,
                sequenceNo);

            // 5. Check idempotency before processing
            var idempotencyKey = $"{commandTypeName}:{message.MessageId}";
            var alreadyProcessed = await _idempotencyService.HasProcessedAsync(
                idempotencyKey,
                cancellationToken);

            if (alreadyProcessed)
            {
                sw.Stop();
                _logger.LogInformation(
                    "Duplicate command detected (idempotency): {CommandType}, MessageId: {MessageId}, Duration: {Duration}ms",
                    commandTypeName, message.MessageId, sw.ElapsedMilliseconds);

                _cloudMetrics.RecordDuplicateDetected(commandTypeName, "aws");
                _cloudTelemetry.RecordSuccess(activity, sw.ElapsedMilliseconds);

                // Delete the duplicate message
                await _sqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle
                }, cancellationToken);

                return;
            }

            // 6. Decrypt message body if encryption is enabled
            var messageBody = message.Body;
            if (_encryption != null)
            {
                messageBody = await _encryption.DecryptAsync(messageBody);
                _logger.LogDebug("Command message decrypted using {Algorithm}",
                    _encryption.AlgorithmName);
            }

            // 7. Record message size
            _cloudMetrics.RecordMessageSize(messageBody.Length, commandTypeName, "aws");

            // 8. Deserialize command
            var command = JsonSerializer.Deserialize(messageBody, commandType, _jsonOptions) as ICommand;

            if (command == null)
            {
                _logger.LogError("Failed to deserialize command: {CommandType}", commandTypeName);
                await CreateDeadLetterRecord(message, queueUrl, "DeserializationFailure",
                    $"Failed to deserialize command of type: {commandTypeName}");
                return;
            }

            // 9. Create scoped service provider for command handling
            using var scope = _serviceProvider.CreateScope();
            var commandSubscriber = scope.ServiceProvider
                .GetRequiredService<ICommandSubscriber>();

            // 10. Invoke Subscribe method using reflection (to preserve generics)
            var subscribeMethod = _methodInfoCache.GetOrAdd(commandType, static t =>
                typeof(ICommandSubscriber).GetMethod("Subscribe")?.MakeGenericMethod(t));

            if (subscribeMethod == null)
            {
                _logger.LogError("Could not find Subscribe method for command type: {CommandType}",
                    commandTypeName);
                await CreateDeadLetterRecord(message, queueUrl, "SubscriptionFailure",
                    $"Could not find Subscribe method for: {commandTypeName}");
                return;
            }

            // 11. Process the command
            await (Task)subscribeMethod.Invoke(commandSubscriber, new[] { command })!;

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
            _cloudMetrics.RecordCommandProcessed(commandTypeName, queueUrl, "aws", success: true);
            _cloudMetrics.RecordProcessingDuration(sw.ElapsedMilliseconds, commandTypeName, "aws");

            // 15. Log with masked sensitive data
            _logger.LogInformation(
                "Command processed from SQS: {CommandType} -> {Queue}, Duration: {Duration}ms, MessageId: {MessageId}, Command: {Command}",
                commandTypeName, queueUrl, sw.ElapsedMilliseconds, message.MessageId,
                _dataMasker.MaskLazy(command));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _cloudTelemetry.RecordError(activity, ex, sw.ElapsedMilliseconds);
            _cloudMetrics.RecordCommandProcessed(commandTypeName, queueUrl, "aws", success: false);

            _logger.LogError(ex,
                "Error processing SQS message: {CommandType}, MessageId: {MessageId}, Duration: {Duration}ms",
                commandTypeName, message.MessageId, sw.ElapsedMilliseconds);

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

    private string? ExtractTraceParent(Dictionary<string, MessageAttributeValue> messageAttributes)
    {
        if (messageAttributes.TryGetValue("traceparent", out var traceParentAttr))
        {
            return traceParentAttr.StringValue;
        }
        return null;
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
                MessageType = message.MessageAttributes.TryGetValue("CommandType", out var cmdType)
                    ? cmdType.StringValue
                    : "Unknown",
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
                Metadata = message.MessageAttributes.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.StringValue)
            };

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
}
