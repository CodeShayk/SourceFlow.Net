using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Core.Configuration;
using SourceFlow.Cloud.AWS.Observability;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Messaging.Commands;

public class AwsSqsCommandDispatcher : ICommandDispatcher
{
    private readonly IAmazonSQS _sqsClient;
    private readonly ICommandRoutingConfiguration _routingConfig;
    private readonly ILogger<AwsSqsCommandDispatcher> _logger;
    private readonly IDomainTelemetryService _telemetry;
    private readonly JsonSerializerOptions _jsonOptions;

    public AwsSqsCommandDispatcher(
        IAmazonSQS sqsClient,
        ICommandRoutingConfiguration routingConfig,
        ILogger<AwsSqsCommandDispatcher> logger,
        IDomainTelemetryService telemetry)
    {
        _sqsClient = sqsClient;
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
        // 1. Check if this command type should be routed to AWS
        if (!_routingConfig.ShouldRoute<TCommand>())
            return; // Skip this dispatcher

        try
        {
            // 2. Get queue URL for command type
            var queueUrl = _routingConfig.GetQueueName<TCommand>();

            // 3. Serialize command to JSON
            var messageBody = JsonSerializer.Serialize(command, _jsonOptions);

            // 4. Create SQS message attributes
            var messageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["CommandType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = typeof(TCommand).AssemblyQualifiedName
                },
                ["EntityId"] = new MessageAttributeValue
                {
                    DataType = "String", // Changed to string to avoid JSON number parsing issues
                    StringValue = command.Entity?.Id.ToString()
                },
                ["SequenceNo"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = command.Metadata?.SequenceNo.ToString()
                }
            };

            // 5. Send to SQS
            var request = new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = messageBody,
                MessageAttributes = messageAttributes,
                MessageGroupId = command.Entity?.Id.ToString() ?? Guid.NewGuid().ToString() // FIFO ordering
            };

            await _sqsClient.SendMessageAsync(request);

            // 6. Log and telemetry
            _logger.LogInformation("Command sent to SQS: {Command} -> {Queue}",
                typeof(TCommand).Name, queueUrl);
            _telemetry.RecordAwsCommandDispatched(typeof(TCommand).Name, queueUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending command to SQS: {CommandType}", typeof(TCommand).Name);
            throw;
        }
    }
}