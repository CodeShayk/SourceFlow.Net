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
using SourceFlow.Messaging.Events;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.Azure.Messaging.Events;

/// <summary>
/// Enhanced Azure Service Bus Event Listener with idempotency, tracing, metrics, and dead letter handling
/// </summary>
public class AzureServiceBusEventListenerEnhanced : BackgroundService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAzureEventRoutingConfiguration _routingConfig;
    private readonly ILogger<AzureServiceBusEventListenerEnhanced> _logger;
    private readonly CloudTelemetry _cloudTelemetry;
    private readonly CloudMetrics _cloudMetrics;
    private readonly IIdempotencyService _idempotencyService;
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly IMessageEncryption? _encryption;
    private readonly SensitiveDataMasker _dataMasker;
    private readonly List<ServiceBusProcessor> _processors;
    private readonly JsonSerializerOptions _jsonOptions;

    public AzureServiceBusEventListenerEnhanced(
        ServiceBusClient serviceBusClient,
        IServiceProvider serviceProvider,
        IAzureEventRoutingConfiguration routingConfig,
        ILogger<AzureServiceBusEventListenerEnhanced> logger,
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
        var subscriptions = _routingConfig.GetListeningSubscriptions();

        if (!subscriptions.Any())
        {
            _logger.LogWarning("No Azure Service Bus subscriptions configured for listening");
            return;
        }

        _logger.LogInformation("Starting Azure Service Bus event listener for {Count} subscriptions",
            subscriptions.Count());

        foreach (var (topicName, subscriptionName) in subscriptions)
        {
            var processor = _serviceBusClient.CreateProcessor(topicName, subscriptionName,
                new ServiceBusProcessorOptions
                {
                    MaxConcurrentCalls = 10,
                    AutoCompleteMessages = false,
                    MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5)
                });

            processor.ProcessMessageAsync += async args =>
            {
                await ProcessMessage(args, topicName, subscriptionName, stoppingToken);
            };

            processor.ProcessErrorAsync += async args =>
            {
                _logger.LogError(args.Exception,
                    "Error processing event from topic: {Topic}/{Subscription}",
                    topicName, subscriptionName);
            };

            await processor.StartProcessingAsync(stoppingToken);
            _processors.Add(processor);

            _logger.LogInformation("Started listening to topic: {Topic}/{Subscription}",
                topicName, subscriptionName);
        }

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessMessage(
        ProcessMessageEventArgs args,
        string topicName,
        string subscriptionName,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        string eventTypeName = "Unknown";
        Activity? activity = null;

        try
        {
            var message = args.Message;

            eventTypeName = message.ApplicationProperties.TryGetValue("EventType", out var et)
                ? et?.ToString() ?? "Unknown"
                : "Unknown";

            if (eventTypeName == "Unknown")
            {
                _logger.LogError("Message missing EventType: {MessageId}", message.MessageId);
                await args.DeadLetterMessageAsync(message, "MissingEventType",
                    "Message missing EventType property");
                return;
            }

            var eventType = Type.GetType(eventTypeName);
            if (eventType == null)
            {
                _logger.LogError("Could not resolve event type: {EventType}", eventTypeName);
                await args.DeadLetterMessageAsync(message, "TypeResolutionFailure",
                    $"Could not resolve type: {eventTypeName}");
                return;
            }

            var traceParent = message.ApplicationProperties.TryGetValue("traceparent", out var tp)
                ? tp?.ToString()
                : null;

            long? sequenceNo = message.ApplicationProperties.TryGetValue("SequenceNo", out var seq) &&
                               long.TryParse(seq?.ToString(), out var seqValue) ? seqValue : null;

            activity = _cloudTelemetry.StartEventReceive(
                eventTypeName,
                $"{topicName}/{subscriptionName}",
                "azure",
                traceParent,
                sequenceNo);

            var idempotencyKey = $"{eventTypeName}:{message.MessageId}";
            if (await _idempotencyService.HasProcessedAsync(idempotencyKey, cancellationToken))
            {
                sw.Stop();
                _logger.LogInformation("Duplicate event detected: {EventType}", eventTypeName);
                _cloudMetrics.RecordDuplicateDetected(eventTypeName, "azure");
                _cloudTelemetry.RecordSuccess(activity, sw.ElapsedMilliseconds);
                await args.CompleteMessageAsync(message, cancellationToken);
                return;
            }

            var messageBody = message.Body.ToString();
            if (_encryption != null)
            {
                messageBody = await _encryption.DecryptAsync(messageBody);
            }

            _cloudMetrics.RecordMessageSize(messageBody.Length, eventTypeName, "azure");

            var @event = JsonSerializer.Deserialize(messageBody, eventType, _jsonOptions) as IEvent;
            if (@event == null)
            {
                _logger.LogError("Failed to deserialize event: {EventType}", eventTypeName);
                await args.DeadLetterMessageAsync(message, "DeserializationFailure",
                    $"Failed to deserialize: {eventTypeName}");
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var subscribers = scope.ServiceProvider.GetServices<IEventSubscriber>();
            var method = typeof(IEventSubscriber)
                .GetMethod(nameof(IEventSubscriber.Subscribe))
                ?.MakeGenericMethod(eventType);

            if (method == null)
            {
                _logger.LogError("Could not find Subscribe method: {EventType}", eventTypeName);
                return;
            }

            var tasks = subscribers.Select(sub =>
            {
                try
                {
                    return (Task)method.Invoke(sub, new[] { @event })!;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error invoking Subscribe for: {EventType}", eventTypeName);
                    return Task.CompletedTask;
                }
            });

            await Task.WhenAll(tasks);

            await _idempotencyService.MarkAsProcessedAsync(
                idempotencyKey,
                TimeSpan.FromHours(24),
                cancellationToken);

            await args.CompleteMessageAsync(message, cancellationToken);

            sw.Stop();
            _cloudTelemetry.RecordSuccess(activity, sw.ElapsedMilliseconds);
            _cloudMetrics.RecordEventReceived(eventTypeName, $"{topicName}/{subscriptionName}", "azure");

            _logger.LogInformation(
                "Event processed: {EventType} -> {Topic}/{Subscription}, Duration: {Duration}ms, Event: {Event}",
                eventTypeName, topicName, subscriptionName, sw.ElapsedMilliseconds, _dataMasker.Mask(@event));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _cloudTelemetry.RecordError(activity, ex, sw.ElapsedMilliseconds);
            _logger.LogError(ex, "Error processing event: {EventType}", eventTypeName);

            if (args.Message.DeliveryCount >= 3)
            {
                await CreateDeadLetterRecord(args.Message, topicName, subscriptionName,
                    "ProcessingFailure", ex.Message, ex);
            }

            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    private async Task CreateDeadLetterRecord(
        ServiceBusReceivedMessage message,
        string topicName,
        string subscriptionName,
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
                MessageType = message.ApplicationProperties.TryGetValue("EventType", out var et)
                    ? et?.ToString() ?? "Unknown"
                    : "Unknown",
                Reason = reason,
                ErrorDescription = errorDescription,
                OriginalSource = $"{topicName}/{subscriptionName}",
                DeadLetterSource = $"{topicName}/{subscriptionName}/$DeadLetterQueue",
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
