using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Core.Configuration;
using SourceFlow.Cloud.AWS.Observability;
using SourceFlow.Cloud.Core.Observability;
using SourceFlow.Cloud.Core.Resilience;
using SourceFlow.Cloud.Core.Security;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Messaging.Commands;

/// <summary>
/// Enhanced AWS SQS Command Dispatcher with tracing, metrics, circuit breaker, and encryption
/// </summary>
public class AwsSqsCommandDispatcherEnhanced : ICommandDispatcher
{
    private readonly IAmazonSQS _sqsClient;
    private readonly ICommandRoutingConfiguration _routingConfig;
    private readonly ILogger<AwsSqsCommandDispatcherEnhanced> _logger;
    private readonly IDomainTelemetryService _domainTelemetry;
    private readonly CloudTelemetry _cloudTelemetry;
    private readonly CloudMetrics _cloudMetrics;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly IMessageEncryption? _encryption;
    private readonly SensitiveDataMasker _dataMasker;
    private readonly JsonSerializerOptions _jsonOptions;

    public AwsSqsCommandDispatcherEnhanced(
        IAmazonSQS sqsClient,
        ICommandRoutingConfiguration routingConfig,
        ILogger<AwsSqsCommandDispatcherEnhanced> logger,
        IDomainTelemetryService domainTelemetry,
        CloudTelemetry cloudTelemetry,
        CloudMetrics cloudMetrics,
        ICircuitBreaker circuitBreaker,
        SensitiveDataMasker dataMasker,
        IMessageEncryption? encryption = null)
    {
        _sqsClient = sqsClient;
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

    public async Task Dispatch<TCommand>(TCommand command) where TCommand : ICommand
    {
        // Check if this command type should be routed to AWS
        if (!_routingConfig.ShouldRoute<TCommand>())
            return;

        var commandType = typeof(TCommand).Name;
        var queueUrl = _routingConfig.GetQueueName<TCommand>();
        var sw = Stopwatch.StartNew();

        // Start distributed trace activity
        using var activity = _cloudTelemetry.StartCommandDispatch(
            commandType,
            queueUrl,
            "aws",
            command.Entity?.Id,
            command.Metadata?.SequenceNo);

        try
        {
            // Execute with circuit breaker protection
            await _circuitBreaker.ExecuteAsync(async () =>
            {
                // Serialize command to JSON
                var messageBody = JsonSerializer.Serialize(command, _jsonOptions);

                // Encrypt if encryption is enabled
                if (_encryption != null)
                {
                    messageBody = await _encryption.EncryptAsync(messageBody);
                    _logger.LogDebug("Command message encrypted using {Algorithm}",
                        _encryption.AlgorithmName);
                }

                // Record message size
                _cloudMetrics.RecordMessageSize(
                    messageBody.Length,
                    commandType,
                    "aws");

                // Create SQS message attributes
                var messageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["CommandType"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = typeof(TCommand).AssemblyQualifiedName
                    },
                    ["EntityId"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = command.Entity?.Id.ToString()
                    },
                    ["SequenceNo"] = new MessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = command.Metadata?.SequenceNo.ToString()
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

                // Create SQS request
                var request = new SendMessageRequest
                {
                    QueueUrl = queueUrl,
                    MessageBody = messageBody,
                    MessageAttributes = messageAttributes,
                    MessageGroupId = command.Entity?.Id.ToString() ?? Guid.NewGuid().ToString(),
                    MessageSystemAttributes = new Dictionary<string, MessageSystemAttributeValue>
                    {
                        ["AWSTraceHeader"] = new MessageSystemAttributeValue
                        {
                            DataType = "String",
                            StringValue = activity?.Id
                        }
                    }
                };

                // Send to SQS
                await _sqsClient.SendMessageAsync(request);

                return true;
            });

            // Record success
            sw.Stop();
            _cloudTelemetry.RecordSuccess(activity, sw.ElapsedMilliseconds);
            _cloudMetrics.RecordCommandDispatched(commandType, queueUrl, "aws");
            _cloudMetrics.RecordDispatchDuration(sw.ElapsedMilliseconds, commandType, "aws");
            _domainTelemetry.RecordAwsCommandDispatched(commandType, queueUrl);

            // Log with masked sensitive data
            _logger.LogInformation("Command dispatched to AWS SQS: {CommandType} -> {Queue}, Duration: {Duration}ms, Command: {Command}",
                commandType, queueUrl, sw.ElapsedMilliseconds, _dataMasker.Mask(command));
        }
        catch (CircuitBreakerOpenException cbex)
        {
            sw.Stop();
            _cloudTelemetry.RecordError(activity, cbex, sw.ElapsedMilliseconds);

            _logger.LogWarning(cbex,
                "Circuit breaker is open for AWS SQS. Command dispatch blocked: {CommandType}, RetryAfter: {RetryAfter}s",
                commandType, cbex.RetryAfter.TotalSeconds);

            // Note: In a real implementation, you might want to fallback to local processing here
            // if hybrid mode is enabled
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _cloudTelemetry.RecordError(activity, ex, sw.ElapsedMilliseconds);

            _logger.LogError(ex,
                "Error dispatching command to AWS SQS: {CommandType}, Queue: {Queue}, Duration: {Duration}ms",
                commandType, queueUrl, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
