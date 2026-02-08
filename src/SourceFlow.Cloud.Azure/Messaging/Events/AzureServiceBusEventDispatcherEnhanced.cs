using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Configuration;
using SourceFlow.Cloud.Azure.Messaging.Serialization;
using SourceFlow.Cloud.Azure.Observability;
using SourceFlow.Cloud.Core.Observability;
using SourceFlow.Cloud.Core.Resilience;
using SourceFlow.Cloud.Core.Security;
using SourceFlow.Messaging.Events;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.Azure.Messaging.Events;

/// <summary>
/// Enhanced Azure Service Bus Event Dispatcher with tracing, metrics, circuit breaker, and encryption
/// </summary>
public class AzureServiceBusEventDispatcherEnhanced : IEventDispatcher, IAsyncDisposable
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IAzureEventRoutingConfiguration _routingConfig;
    private readonly ILogger<AzureServiceBusEventDispatcherEnhanced> _logger;
    private readonly CloudTelemetry _cloudTelemetry;
    private readonly CloudMetrics _cloudMetrics;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly IMessageEncryption? _encryption;
    private readonly SensitiveDataMasker _dataMasker;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senderCache;
    private readonly JsonSerializerOptions _jsonOptions;

    public AzureServiceBusEventDispatcherEnhanced(
        ServiceBusClient serviceBusClient,
        IAzureEventRoutingConfiguration routingConfig,
        ILogger<AzureServiceBusEventDispatcherEnhanced> logger,
        CloudTelemetry cloudTelemetry,
        CloudMetrics cloudMetrics,
        ICircuitBreaker circuitBreaker,
        SensitiveDataMasker dataMasker,
        IMessageEncryption? encryption = null)
    {
        _serviceBusClient = serviceBusClient;
        _routingConfig = routingConfig;
        _logger = logger;
        _cloudTelemetry = cloudTelemetry;
        _cloudMetrics = cloudMetrics;
        _circuitBreaker = circuitBreaker;
        _encryption = encryption;
        _dataMasker = dataMasker;
        _senderCache = new ConcurrentDictionary<string, ServiceBusSender>();
        _jsonOptions = JsonOptions.Default;
    }

    public async Task Dispatch<TEvent>(TEvent @event) where TEvent : IEvent
    {
        if (!_routingConfig.ShouldRouteToAzure<TEvent>())
            return;

        var eventType = typeof(TEvent).Name;
        var topicName = _routingConfig.GetTopicName<TEvent>();
        var sw = Stopwatch.StartNew();

        using var activity = _cloudTelemetry.StartEventPublish(
            eventType,
            topicName,
            "azure",
            @event.Metadata?.SequenceNo);

        try
        {
            await _circuitBreaker.ExecuteAsync(async () =>
            {
                var sender = _senderCache.GetOrAdd(topicName,
                    name => _serviceBusClient.CreateSender(name));

                var messageBody = JsonSerializer.Serialize(@event, _jsonOptions);

                if (_encryption != null)
                {
                    messageBody = await _encryption.EncryptAsync(messageBody);
                    _logger.LogDebug("Event encrypted using {Algorithm}", _encryption.AlgorithmName);
                }

                _cloudMetrics.RecordMessageSize(messageBody.Length, eventType, "azure");

                var message = new ServiceBusMessage(messageBody)
                {
                    MessageId = Guid.NewGuid().ToString(),
                    Subject = @event.Name,
                    ContentType = "application/json"
                };

                message.ApplicationProperties["EventType"] = typeof(TEvent).AssemblyQualifiedName;
                message.ApplicationProperties["EventName"] = @event.Name;
                message.ApplicationProperties["SequenceNo"] = @event.Metadata?.SequenceNo;

                var traceContext = new Dictionary<string, string>();
                _cloudTelemetry.InjectTraceContext(activity, traceContext);
                foreach (var kvp in traceContext)
                {
                    message.ApplicationProperties[kvp.Key] = kvp.Value;
                }

                await sender.SendMessageAsync(message);
                return true;
            });

            sw.Stop();
            _cloudTelemetry.RecordSuccess(activity, sw.ElapsedMilliseconds);
            _cloudMetrics.RecordEventPublished(eventType, topicName, "azure");
            _cloudMetrics.RecordPublishDuration(sw.ElapsedMilliseconds, eventType, "azure");

            _logger.LogInformation(
                "Event published to Azure Service Bus: {EventType} -> {Topic}, Duration: {Duration}ms, Event: {Event}",
                eventType, topicName, sw.ElapsedMilliseconds, _dataMasker.Mask(@event));
        }
        catch (CircuitBreakerOpenException cbex)
        {
            sw.Stop();
            _cloudTelemetry.RecordError(activity, cbex, sw.ElapsedMilliseconds);
            _logger.LogWarning(cbex,
                "Circuit breaker is open for Azure Service Bus. Event publish blocked: {EventType}",
                eventType);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _cloudTelemetry.RecordError(activity, ex, sw.ElapsedMilliseconds);
            _logger.LogError(ex,
                "Error publishing event to Azure Service Bus: {EventType}, Topic: {Topic}",
                eventType, topicName);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senderCache.Values)
        {
            await sender.DisposeAsync();
        }
        _senderCache.Clear();
    }
}
