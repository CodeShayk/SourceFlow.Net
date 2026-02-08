using System.Diagnostics;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.AWS.Observability;
using SourceFlow.Cloud.Core.Observability;
using SourceFlow.Cloud.Core.Resilience;
using SourceFlow.Cloud.Core.Security;
using SourceFlow.Messaging.Events;
using SourceFlow.Observability;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Messaging.Events;

/// <summary>
/// Enhanced AWS SNS Event Dispatcher with tracing, metrics, circuit breaker, and encryption
/// </summary>
public class AwsSnsEventDispatcherEnhanced : IEventDispatcher
{
    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly IAwsEventRoutingConfiguration _routingConfig;
    private readonly ILogger<AwsSnsEventDispatcherEnhanced> _logger;
    private readonly IDomainTelemetryService _domainTelemetry;
    private readonly CloudTelemetry _cloudTelemetry;
    private readonly CloudMetrics _cloudMetrics;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly IMessageEncryption? _encryption;
    private readonly SensitiveDataMasker _dataMasker;
    private readonly JsonSerializerOptions _jsonOptions;

    public AwsSnsEventDispatcherEnhanced(
        IAmazonSimpleNotificationService snsClient,
        IAwsEventRoutingConfiguration routingConfig,
        ILogger<AwsSnsEventDispatcherEnhanced> logger,
        IDomainTelemetryService domainTelemetry,
        CloudTelemetry cloudTelemetry,
        CloudMetrics cloudMetrics,
        ICircuitBreaker circuitBreaker,
        SensitiveDataMasker dataMasker,
        IMessageEncryption? encryption = null)
    {
        _snsClient = snsClient;
        _routingConfig = routingConfig;
        _logger = logger;
        _domainTelemetry = domainTelemetry;
        _cloudTelemetry = cloudTelemetry;
        _cloudMetrics = cloudMetrics;
        _circuitBreaker = circuitBreaker;
        _encryption = encryption;
        _dataMasker = dataMasker;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task Dispatch<TEvent>(TEvent @event) where TEvent : IEvent
    {
        // Check if this event type should be routed to AWS
        if (!_routingConfig.ShouldRouteToAws<TEvent>())
            return;

        var eventType = typeof(TEvent).Name;
        var topicArn = _routingConfig.GetTopicArn<TEvent>();
        var sw = Stopwatch.StartNew();

        // Start distributed trace activity
        using var activity = _cloudTelemetry.StartEventPublish(
            eventType,
            topicArn,
            "aws",
            @event.Metadata?.SequenceNo);

        try
        {
            // Execute with circuit breaker protection
            await _circuitBreaker.ExecuteAsync(async () =>
            {
                // Serialize event to JSON
                var messageBody = JsonSerializer.Serialize(@event, _jsonOptions);

                // Encrypt if encryption is enabled
                if (_encryption != null)
                {
                    messageBody = await _encryption.EncryptAsync(messageBody);
                    _logger.LogDebug("Event message encrypted using {Algorithm}",
                        _encryption.AlgorithmName);
                }

                // Record message size
                _cloudMetrics.RecordMessageSize(
                    messageBody.Length,
                    eventType,
                    "aws");

                // Create SNS message attributes
                var messageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["EventType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = typeof(TEvent).AssemblyQualifiedName
                    },
                    ["EventName"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = @event.Name
                    },
                    ["SequenceNo"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = @event.Metadata?.SequenceNo.ToString()
                    }
                };

                // Inject trace context
                var traceContext = new Dictionary<string, string>();
                _cloudTelemetry.InjectTraceContext(activity, traceContext);
                foreach (var kvp in traceContext)
                {
                    messageAttributes[kvp.Key] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = kvp.Value
                    };
                }

                // Create SNS request
                var request = new PublishRequest
                {
                    TopicArn = topicArn,
                    Message = messageBody,
                    MessageAttributes = messageAttributes,
                    Subject = @event.Name
                };

                // Publish to SNS
                await _snsClient.PublishAsync(request);

                return true;
            });

            // Record success
            sw.Stop();
            _cloudTelemetry.RecordSuccess(activity, sw.ElapsedMilliseconds);
            _cloudMetrics.RecordEventPublished(eventType, topicArn, "aws");
            _cloudMetrics.RecordPublishDuration(sw.ElapsedMilliseconds, eventType, "aws");

            // Log with masked sensitive data
            _logger.LogInformation(
                "Event published to AWS SNS: {EventType} -> {Topic}, Duration: {Duration}ms, Event: {Event}",
                eventType, topicArn, sw.ElapsedMilliseconds, _dataMasker.Mask(@event));
        }
        catch (CircuitBreakerOpenException cbex)
        {
            sw.Stop();
            _cloudTelemetry.RecordError(activity, cbex, sw.ElapsedMilliseconds);

            _logger.LogWarning(cbex,
                "Circuit breaker is open for AWS SNS. Event publish blocked: {EventType}, RetryAfter: {RetryAfter}s",
                eventType, cbex.RetryAfter.TotalSeconds);

            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _cloudTelemetry.RecordError(activity, ex, sw.ElapsedMilliseconds);

            _logger.LogError(ex,
                "Error publishing event to AWS SNS: {EventType}, Topic: {Topic}, Duration: {Duration}ms",
                eventType, topicArn, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
