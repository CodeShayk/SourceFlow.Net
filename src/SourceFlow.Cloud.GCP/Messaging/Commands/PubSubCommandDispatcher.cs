using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.GCP.Observability;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;
using System.Text.Json;

namespace SourceFlow.Cloud.GCP.Messaging.Commands;

/// <summary>
/// Dispatches commands to Google Cloud Pub/Sub by publishing to the resolved command topic.
/// </summary>
public class PubSubCommandDispatcher : ICommandDispatcher
{
    private readonly PublisherServiceApiClient _publisher;
    private readonly ICommandRoutingConfiguration _routingConfig;
    private readonly ILogger<PubSubCommandDispatcher> _logger;
    private readonly IDomainTelemetryService _telemetry;
    private readonly JsonSerializerOptions _jsonOptions;

    public PubSubCommandDispatcher(
        PublisherServiceApiClient publisher,
        ICommandRoutingConfiguration routingConfig,
        ILogger<PubSubCommandDispatcher> logger,
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

    public async Task Dispatch<TCommand>(TCommand command) where TCommand : ICommand
    {
        // 1. Check if this command type should be routed to GCP
        if (!_routingConfig.ShouldRoute<TCommand>())
            return; // Skip this dispatcher

        try
        {
            // 2. Resolve the target topic (full resource name) for the command type
            var topicName = TopicName.Parse(_routingConfig.GetQueueName<TCommand>());

            // 3. Serialize command to JSON
            var messageBody = JsonSerializer.Serialize(command, _jsonOptions);

            // 4. Build the Pub/Sub message with routing attributes
            var message = new PubsubMessage
            {
                Data = ByteString.CopyFromUtf8(messageBody),
                // Ordering key gives per-entity ordering on subscriptions with message ordering enabled.
                OrderingKey = command.Entity?.Id.ToString() ?? string.Empty,
                Attributes =
                {
                    ["CommandType"] = typeof(TCommand).AssemblyQualifiedName ?? typeof(TCommand).FullName ?? typeof(TCommand).Name,
                    ["EntityId"] = command.Entity?.Id.ToString() ?? string.Empty,
                    ["SequenceNo"] = command.Metadata?.SequenceNo.ToString() ?? string.Empty
                }
            };

            // 5. Publish to the topic
            await _publisher.PublishAsync(topicName, new[] { message });

            // 6. Log and telemetry
            _logger.LogInformation("Command published to Pub/Sub: {Command} -> {Topic}",
                typeof(TCommand).Name, topicName);
            _telemetry.RecordGcpCommandDispatched(typeof(TCommand).Name, topicName.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing command to Pub/Sub: {CommandType}", typeof(TCommand).Name);
            throw;
        }
    }
}
