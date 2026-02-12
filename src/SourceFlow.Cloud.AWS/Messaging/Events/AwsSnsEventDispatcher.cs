using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Core.Configuration;
using SourceFlow.Cloud.AWS.Observability;
using SourceFlow.Messaging.Events;
using SourceFlow.Observability;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Messaging.Events;

public class AwsSnsEventDispatcher : IEventDispatcher
{
    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly IEventRoutingConfiguration _routingConfig;
    private readonly ILogger<AwsSnsEventDispatcher> _logger;
    private readonly IDomainTelemetryService _telemetry;
    private readonly JsonSerializerOptions _jsonOptions;

    public AwsSnsEventDispatcher(
        IAmazonSimpleNotificationService snsClient,
        IEventRoutingConfiguration routingConfig,
        ILogger<AwsSnsEventDispatcher> logger,
        IDomainTelemetryService telemetry)
    {
        _snsClient = snsClient;
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
        // 1. Check if this event type should be routed to AWS
        if (!_routingConfig.ShouldRoute<TEvent>())
            return; // Skip this dispatcher

        try
        {
            // 2. Get topic ARN for event type
            var topicArn = _routingConfig.GetTopicName<TEvent>();

            // 3. Serialize event to JSON
            var messageBody = JsonSerializer.Serialize(@event, _jsonOptions);

            // 4. Create SNS message attributes
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
                }
            };

            // 5. Publish to SNS
            var request = new PublishRequest
            {
                TopicArn = topicArn,
                Message = messageBody,
                MessageAttributes = messageAttributes,
                Subject = @event.Name
            };

            var response = await _snsClient.PublishAsync(request);

            // 6. Log and telemetry
            _logger.LogInformation("Event published to SNS: {Event} -> {Topic}, MessageId: {MessageId}",
                typeof(TEvent).Name, topicArn, response.MessageId);
            _telemetry.RecordAwsEventPublished(typeof(TEvent).Name, topicArn);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing event to SNS: {EventType}", typeof(TEvent).Name);
            throw;
        }
    }
}