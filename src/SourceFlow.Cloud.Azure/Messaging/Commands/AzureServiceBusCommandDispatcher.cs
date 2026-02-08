using System.Text.Json;
using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Configuration;
using SourceFlow.Cloud.Azure.Observability;
using SourceFlow.Cloud.Azure.Messaging.Serialization;
using SourceFlow.Messaging.Commands;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.Azure.Messaging.Commands;

public class AzureServiceBusCommandDispatcher : ICommandDispatcher, IAsyncDisposable
{
    private readonly ServiceBusClient serviceBusClient;
    private readonly IAzureCommandRoutingConfiguration routingConfig;
    private readonly ILogger<AzureServiceBusCommandDispatcher> logger;
    private readonly IDomainTelemetryService telemetry;
    private readonly ConcurrentDictionary<string, ServiceBusSender> senderCache;

    public AzureServiceBusCommandDispatcher(
        ServiceBusClient serviceBusClient,
        IAzureCommandRoutingConfiguration routingConfig,
        ILogger<AzureServiceBusCommandDispatcher> logger,
        IDomainTelemetryService telemetry)
    {
        this.serviceBusClient = serviceBusClient;
        this.routingConfig = routingConfig;
        this.logger = logger;
        this.telemetry = telemetry;
        this.senderCache = new ConcurrentDictionary<string, ServiceBusSender>();
    }

    public async Task Dispatch<TCommand>(TCommand command)
        where TCommand : ICommand
    {
        // 1. Check if this command type should be routed to Azure
        if (!routingConfig.ShouldRouteToAzure<TCommand>())
            return; // Skip this dispatcher

        // 2. Get queue name for command type
        var queueName = routingConfig.GetQueueName<TCommand>();

        // 3. Get or create sender for this queue
        var sender = senderCache.GetOrAdd(queueName,
            name => serviceBusClient.CreateSender(name));

        // 4. Serialize command to JSON
        var messageBody = JsonSerializer.Serialize(command, JsonOptions.Default);

        // 5. Create Service Bus message
        var message = new ServiceBusMessage(messageBody)
        {
            MessageId = Guid.NewGuid().ToString(),
            SessionId = command.Entity.Id.ToString(), // For session-based ordering
            Subject = command.Name,
            ContentType = "application/json",
            ApplicationProperties =
            {
                ["CommandType"] = typeof(TCommand).AssemblyQualifiedName,
                ["EntityId"] = command.Entity.Id,
                ["SequenceNo"] = command.Metadata.SequenceNo,
                ["IsReplay"] = command.Metadata.IsReplay
            }
        };

        // 6. Send to Service Bus Queue
        await sender.SendMessageAsync(message);

        // 7. Log and telemetry
        logger.LogInformation(
            "Command sent to Azure Service Bus: {Command} -> Queue: {Queue}, MessageId: {MessageId}",
            typeof(TCommand).Name, queueName, message.MessageId);

        telemetry.RecordAzureCommandDispatched(typeof(TCommand).Name, queueName);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in senderCache.Values)
        {
            await sender.DisposeAsync();
        }
        senderCache.Clear();
    }
}