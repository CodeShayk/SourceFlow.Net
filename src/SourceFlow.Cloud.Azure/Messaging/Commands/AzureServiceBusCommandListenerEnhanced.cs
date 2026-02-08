using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Cloud.Azure.Configuration;
using SourceFlow.Cloud.Azure.Messaging.Serialization;
using SourceFlow.Cloud.Azure.Observability;
using SourceFlow.Cloud.Core.Configuration;
using SourceFlow.Cloud.Core.DeadLetter;
using SourceFlow.Cloud.Core.Observability;
using SourceFlow.Cloud.Core.Security;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.Azure.Messaging.Commands;

/// <summary>
/// Enhanced Azure Service Bus Command Listener with idempotency, tracing, metrics, and dead letter handling
/// </summary>
public class AzureServiceBusCommandListenerEnhanced : BackgroundService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAzureCommandRoutingConfiguration _routingConfig;
    private readonly ILogger<AzureServiceBusCommandListenerEnhanced> _logger;
    private readonly IDomainTelemetryService _domainTelemetry;
    private readonly CloudTelemetry _cloudTelemetry;
    private readonly CloudMetrics _cloudMetrics;
    private readonly IIdempotencyService _idempotencyService;
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly IMessageEncryption? _encryption;
    private readonly SensitiveDataMasker _dataMasker;
    private readonly List<ServiceBusProcessor> _processors;
    private readonly JsonSerializerOptions _jsonOptions;

    public AzureServiceBusCommandListenerEnhanced(
        ServiceBusClient serviceBusClient,
        IServiceProvider serviceProvider,
        IAzureCommandRoutingConfiguration routingConfig,
        ILogger<AzureServiceBusCommandListenerEnhanced> logger,
        IDomainTelemetryService domainTelemetry,
        CloudTelemetry cloudTelemetry,
        CloudMetrics cloudMetrics,
        IIdempotencyService idempotencyService,
        IDeadLetterStore deadLetterStore,
        SensitiveDataMasker dataMasker,
        IMessageEncryption? encryption = null)
    {
        _serviceBusClient = serviceBusClient;
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
        _processors = new List<ServiceBusProcessor>();
        _jsonOptions = JsonOptions.Default;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queueNames = _routingConfig.GetListeningQueues();

        if (!queueNames.Any())
        {
            _logger.LogWarning("No Azure Service Bus queues configured for listening");
            return;
        }

        var queueCount = queueNames.Count();
        _logger.LogInformation("Starting Azure Service Bus command listener for {QueueCount} queues", queueCount);

        // Create processor for each queue
        foreach (var queueName in queueNames)
        {
            var processor = _serviceBusClient.CreateProcessor(queueName, new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = 10,
                AutoCompleteMessages = false,
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5),
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });

            processor.ProcessMessageAsync += async args =>
            {
                await ProcessMessage(args, queueName, stoppingToken);
            };

            processor.ProcessErrorAsync += async args =>
            {
                _logger.LogError(args.Exception,
                    "Error processing message from queue: {Queue}, Source: {Source}",
                    queueName, args.ErrorSource);
            };

            await processor.StartProcessingAsync(stoppingToken);
            _processors.Add(processor);

            _logger.LogInformation("Started listening to Azure Service Bus queue: {Queue}", queueName);
        }

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessMessage(
        ProcessMessageEventArgs args,
        string queueName,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        string commandTypeName = "Unknown";
        Activity? activity = null;

        try
        {
            var message = args.Message;

            // Get command type
            commandTypeName = message.ApplicationProperties.TryGetValue("CommandType", out var cmdType)
                ? cmdType?.ToString() ?? "Unknown"
                : "Unknown";

            if (commandTypeName == "Unknown" || !message.ApplicationProperties.ContainsKey("CommandType"))
            {
                _logger.LogError("Message missing CommandType: {MessageId}", message.MessageId);
                await args.DeadLetterMessageAsync(message, "MissingCommandType",
                    "Message is missing CommandType property");
                await CreateDeadLetterRecord(message, queueName, "MissingCommandType",
                    "Message is missing CommandType property");
                return;
            }

            var commandType = Type.GetType(commandTypeName);
            if (commandType == null)
            {
                _logger.LogError("Could not resolve command type: {CommandType}", commandTypeName);
                await args.DeadLetterMessageAsync(message, "TypeResolutionFailure",
                    $"Could not resolve type: {commandTypeName}");
                await CreateDeadLetterRecord(message, queueName, "TypeResolutionFailure",
                    $"Could not resolve type: {commandTypeName}");
                return;
            }

            // Extract trace context
            var traceParent = message.ApplicationProperties.TryGetValue("traceparent", out var tp)
                ? tp?.ToString()
                : null;

            // Extract entity ID and sequence number
            object? entityId = message.ApplicationProperties.TryGetValue("EntityId", out var eid) ? eid : null;
            long? sequenceNo = message.ApplicationProperties.TryGetValue("SequenceNo", out var seq) &&
                               long.TryParse(seq?.ToString(), out var seqValue) ? seqValue : null;

            // Start distributed trace
            activity = _cloudTelemetry.StartCommandProcess(
                commandTypeName,
                queueName,
                "azure",
                traceParent,
                entityId,
                sequenceNo);

            // Check idempotency
            var idempotencyKey = $"{commandTypeName}:{message.MessageId}";
            if (await _idempotencyService.HasProcessedAsync(idempotencyKey, cancellationToken))
            {
                sw.Stop();
                _logger.LogInformation(
                    "Duplicate command detected: {CommandType}, MessageId: {MessageId}",
                    commandTypeName, message.MessageId);

                _cloudMetrics.RecordDuplicateDetected(commandTypeName, "azure");
                _cloudTelemetry.RecordSuccess(activity, sw.ElapsedMilliseconds);

                await args.CompleteMessageAsync(message, cancellationToken);
                return;
            }

            // Decrypt if needed
            var messageBody = message.Body.ToString();
            if (_encryption != null)
            {
                messageBody = await _encryption.DecryptAsync(messageBody);
                _logger.LogDebug("Command decrypted using {Algorithm}", _encryption.AlgorithmName);
            }

            // Record message size
            _cloudMetrics.RecordMessageSize(messageBody.Length, commandTypeName, "azure");

            // Deserialize command
            var command = JsonSerializer.Deserialize(messageBody, commandType, _jsonOptions) as ICommand;
            if (command == null)
            {
                _logger.LogError("Failed to deserialize: {CommandType}", commandTypeName);
                await args.DeadLetterMessageAsync(message, "DeserializationFailure",
                    $"Failed to deserialize: {commandTypeName}");
                await CreateDeadLetterRecord(message, queueName, "DeserializationFailure",
                    $"Failed to deserialize: {commandTypeName}");
                return;
            }

            // Process command
            using var scope = _serviceProvider.CreateScope();
            var subscriber = scope.ServiceProvider.GetRequiredService<ICommandSubscriber>();
            var method = typeof(ICommandSubscriber)
                .GetMethod(nameof(ICommandSubscriber.Subscribe))
                ?.MakeGenericMethod(commandType);

            if (method == null)
            {
                _logger.LogError("Could not find Subscribe method: {CommandType}", commandTypeName);
                await args.DeadLetterMessageAsync(message, "SubscriptionFailure",
                    $"No Subscribe method for: {commandTypeName}");
                return;
            }

            await (Task)method.Invoke(subscriber, new[] { command })!;

            // Mark as processed
            await _idempotencyService.MarkAsProcessedAsync(
                idempotencyKey,
                TimeSpan.FromHours(24),
                cancellationToken);

            // Complete message
            await args.CompleteMessageAsync(message, cancellationToken);

            // Record success
            sw.Stop();
            _cloudTelemetry.RecordSuccess(activity, sw.ElapsedMilliseconds);
            _cloudMetrics.RecordCommandProcessed(commandTypeName, queueName, "azure", success: true);
            _cloudMetrics.RecordProcessingDuration(sw.ElapsedMilliseconds, commandTypeName, "azure");

            _logger.LogInformation(
                "Command processed: {CommandType} -> {Queue}, Duration: {Duration}ms, Command: {Command}",
                commandTypeName, queueName, sw.ElapsedMilliseconds, _dataMasker.Mask(command));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _cloudTelemetry.RecordError(activity, ex, sw.ElapsedMilliseconds);
            _cloudMetrics.RecordCommandProcessed(commandTypeName, queueName, "azure", success: false);

            _logger.LogError(ex,
                "Error processing command: {CommandType}, MessageId: {MessageId}",
                commandTypeName, args.Message.MessageId);

            // Create dead letter record if delivery count is high
            if (args.Message.DeliveryCount >= 3)
            {
                await CreateDeadLetterRecord(args.Message, queueName, "ProcessingFailure",
                    ex.Message, ex);
            }

            throw; // Let Service Bus handle retry
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private async Task CreateDeadLetterRecord(
        ServiceBusReceivedMessage message,
        string queueName,
        string reason,
        string errorDescription,
        Exception? exception = null)
    {
        try
        {
            var record = new DeadLetterRecord
            {
                MessageId = message.MessageId,
                Body = message.Body.ToString(),
                MessageType = message.ApplicationProperties.TryGetValue("CommandType", out var ct)
                    ? ct?.ToString() ?? "Unknown"
                    : "Unknown",
                Reason = reason,
                ErrorDescription = errorDescription,
                OriginalSource = queueName,
                DeadLetterSource = $"{queueName}/$DeadLetterQueue",
                CloudProvider = "azure",
                DeadLetteredAt = DateTime.UtcNow,
                DeliveryCount = (int)message.DeliveryCount,
                ExceptionType = exception?.GetType().FullName,
                ExceptionMessage = exception?.Message,
                ExceptionStackTrace = exception?.StackTrace,
                Metadata = new Dictionary<string, string>()
            };

            foreach (var prop in message.ApplicationProperties)
            {
                record.Metadata[prop.Key] = prop.Value?.ToString() ?? string.Empty;
            }

            await _deadLetterStore.SaveAsync(record);

            _logger.LogWarning(
                "Dead letter record created: {MessageId}, Type: {MessageType}, Reason: {Reason}",
                record.MessageId, record.MessageType, record.Reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create dead letter record: {MessageId}", message.MessageId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var processor in _processors)
        {
            await processor.StopProcessingAsync(cancellationToken);
            await processor.DisposeAsync();
        }
        _processors.Clear();

        await base.StopAsync(cancellationToken);
    }
}
