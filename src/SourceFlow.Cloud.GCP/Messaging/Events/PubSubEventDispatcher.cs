using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.GCP.Observability;
using SourceFlow.Messaging.Events;
using SourceFlow.Observability;
using System.Text.Json;

namespace SourceFlow.Cloud.GCP.Messaging.Events;

/// <summary>
/// Publishes events to Google Cloud Pub/Sub topics. Each subscriber service receives the event
/// through its own pull subscription created by <c>GcpBusBootstrapper</c>.
/// </summary>
public class PubSubEventDispatcher : IEventDispatcher
{
    private readonly PublisherServiceApiClient _publisher;
    private readonly IEventRoutingConfiguration _routingConfig;
    private readonly ILogger<PubSubEventDispatcher> _logger;
    private readonly IDomainTelemetryService _telemetry;
    private readonly JsonSerializerOptions _jsonOptions;

    public PubSubEventDispatcher(
        PublisherServiceApiClient publisher,
        IEventRoutingConfiguration routingConfig,
        ILogger<PubSubEventDispatcher> logger,
        IDomainTelemetryService telemetry)
    {
        _publisher = publisher;
        _routingConfig = routingConfig;
        _logger = logger;
        _telemetry = telemetry;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task Dispatch<TEvent>(TEvent @event) where TEvent : IEvent
    {
        // 1. Check if this event type should be routed to GCP
        if (!_routingConfig.ShouldRoute<TEvent>())
            return; // Skip this dispatcher

        try
        {
            // 2. Resolve the target topic (full resource name) for the event type
            var topicName = TopicName.Parse(_routingConfig.GetTopicName<TEvent>());

            // 3. Serialize event to JSON
            var messageBody = JsonSerializer.Serialize(@event, _jsonOptions);

            // 4. Build the Pub/Sub message with routing attributes
            var message = new PubsubMessage
            {
                Data = ByteString.CopyFromUtf8(messageBody),
                Attributes =
                {
                    ["EventType"] = typeof(TEvent).AssemblyQualifiedName ?? typeof(TEvent).FullName ?? typeof(TEvent).Name,
                    ["EventName"] = @event.Name ?? string.Empty
                }
            };

            // 5. Publish to the topic
            await _publisher.PublishAsync(topicName, new[] { message });

            // 6. Log and telemetry
            _logger.LogInformation("Event published to Pub/Sub: {Event} -> {Topic}",
                typeof(TEvent).Name, topicName);
            _telemetry.RecordGcpEventPublished(typeof(TEvent).Name, topicName.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing event to Pub/Sub: {EventType}", typeof(TEvent).Name);
            throw;
        }
    }
}
