using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Messaging.Serialization;
using SourceFlow.Cloud.Azure.Observability;
using SourceFlow.Cloud.Core.Configuration;
using SourceFlow.Cloud.Core.Observability;
using SourceFlow.Cloud.Core.Resilience;
using SourceFlow.Cloud.Core.Security;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.Azure.Messaging.Commands;

/// <summary>
/// Enhanced Azure Service Bus Command Dispatcher with tracing, metrics, circuit breaker, and encryption
/// </summary>
public class AzureServiceBusCommandDispatcherEnhanced : ICommandDispatcher, IAsyncDisposable
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ICommandRoutingConfiguration _routingConfig;
    private readonly ILogger<AzureServiceBusCommandDispatcherEnhanced> _logger;
    private readonly IDomainTelemetryService _domainTelemetry;
    private readonly CloudTelemetry _cloudTelemetry;
    private readonly CloudMetrics _cloudMetrics;
    private readonly ICircuitBreaker _circuitBreaker;
    private readonly IMessageEncryption? _encryption;
    private readonly SensitiveDataMasker _dataMasker;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senderCache;
    private readonly JsonSerializerOptions _jsonOptions;

    public AzureServiceBusCommandDispatcherEnhanced(
        ServiceBusClient serviceBusClient,
        ICommandRoutingConfiguration routingConfig,
        ILogger<AzureServiceBusCommandDispatcherEnhanced> logger,
        IDomainTelemetryService domainTelemetry,
        CloudTelemetry cloudTelemetry,
        CloudMetrics cloudMetrics,
        ICircuitBreaker circuitBreaker,
        SensitiveDataMasker dataMasker,
        IMessageEncryption? encryption = null)
    {
        _serviceBusClient = serviceBusClient;
        _routingConfig = routingConfig;
        _logger = logger;
        _domainTelemetry = domainTelemetry;
        _cloudTelemetry = cloudTelemetry;
        _cloudMetrics = cloudMetrics;
        _circuitBreaker = circuitBreaker;
        _encryption = encryption;
        _dataMasker = dataMasker;
        _senderCache = new ConcurrentDictionary<string, ServiceBusSender>();
        _jsonOptions = JsonOptions.Default;
    }

    public async Task Dispatch<TCommand>(TCommand command) where TCommand : ICommand
    {
        // Check if this command type should be routed to Azure
        if (!_routingConfig.ShouldRoute<TCommand>())
            return;

        var commandType = typeof(TCommand).Name;
        var queueName = _routingConfig.GetQueueName<TCommand>();
        var sw = Stopwatch.StartNew();

        // Start distributed trace activity
        using var activity = _cloudTelemetry.StartCommandDispatch(
            commandType,
            queueName,
            "azure",
            command.Entity?.Id,
            command.Metadata?.SequenceNo);

        try
        {
            // Execute with circuit breaker protection
            await _circuitBreaker.ExecuteAsync(async () =>
            {
                // Get or create sender for this queue
                var sender = _senderCache.GetOrAdd(queueName,
                    name => _serviceBusClient.CreateSender(name));

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
                    "azure");

                // Create Service Bus message
                var message = new ServiceBusMessage(messageBody)
                {
                    MessageId = Guid.NewGuid().ToString(),
                    SessionId = command.Entity?.Id.ToString(), // For session-based ordering
                    Subject = command.Name,
                    ContentType = "application/json"
                };

                // Add application properties
                message.ApplicationProperties["CommandType"] = typeof(TCommand).AssemblyQualifiedName;
                message.ApplicationProperties["EntityId"] = command.Entity?.Id.ToString();
                message.ApplicationProperties["SequenceNo"] = command.Metadata?.SequenceNo;
                message.ApplicationProperties["IsReplay"] = command.Metadata?.IsReplay;

                // Inject trace context
                var traceContext = new Dictionary<string, string>();
                _cloudTelemetry.InjectTraceContext(activity, traceContext);
                foreach (var kvp in traceContext)
                {
                    message.ApplicationProperties[kvp.Key] = kvp.Value;
                }

                // Send to Service Bus Queue
                await sender.SendMessageAsync(message);

                return true;
            });

            // Record success
            sw.Stop();
            _cloudTelemetry.RecordSuccess(activity, sw.ElapsedMilliseconds);
            _cloudMetrics.RecordCommandDispatched(commandType, queueName, "azure");
            _cloudMetrics.RecordDispatchDuration(sw.ElapsedMilliseconds, commandType, "azure");

            // Log with masked sensitive data
            _logger.LogInformation(
                "Command dispatched to Azure Service Bus: {CommandType} -> {Queue}, Duration: {Duration}ms, Command: {Command}",
                commandType, queueName, sw.ElapsedMilliseconds, _dataMasker.Mask(command));
        }
        catch (CircuitBreakerOpenException cbex)
        {
            sw.Stop();
            _cloudTelemetry.RecordError(activity, cbex, sw.ElapsedMilliseconds);

            _logger.LogWarning(cbex,
                "Circuit breaker is open for Azure Service Bus. Command dispatch blocked: {CommandType}, RetryAfter: {RetryAfter}s",
                commandType, cbex.RetryAfter.TotalSeconds);

            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _cloudTelemetry.RecordError(activity, ex, sw.ElapsedMilliseconds);

            _logger.LogError(ex,
                "Error dispatching command to Azure Service Bus: {CommandType}, Queue: {Queue}, Duration: {Duration}ms",
                commandType, queueName, sw.ElapsedMilliseconds);
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
