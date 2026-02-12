using System.Text.Json;
using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Azure.Observability;
using SourceFlow.Cloud.Azure.Messaging.Serialization;
using SourceFlow.Cloud.Core.Configuration;
using SourceFlow.Messaging.Events;
using SourceFlow.Observability;

namespace SourceFlow.Cloud.Azure.Messaging.Events;

public class AzureServiceBusEventDispatcher : IEventDispatcher, IAsyncDisposable
{
    private readonly ServiceBusClient serviceBusClient;
    private readonly IEventRoutingConfiguration routingConfig;
    private readonly ILogger<AzureServiceBusEventDispatcher> logger;
    private readonly IDomainTelemetryService telemetry;
    private readonly ConcurrentDictionary<string, ServiceBusSender> senderCache;

    public AzureServiceBusEventDispatcher(
        ServiceBusClient serviceBusClient,
        IEventRoutingConfiguration routingConfig,
        ILogger<AzureServiceBusEventDispatcher> logger,
        IDomainTelemetryService telemetry)
    {
        this.serviceBusClient = serviceBusClient;
        this.routingConfig = routingConfig;
        this.logger = logger;
        this.telemetry = telemetry;
        this.senderCache = new ConcurrentDictionary<string, ServiceBusSender>();
    }

    public async Task Dispatch<TEvent>(TEvent @event)
        where TEvent : IEvent
    {
        // 1. Check if this event type should be routed
        if (!routingConfig.ShouldRoute<TEvent>())
            return; // Skip this dispatcher

        // 2. Get topic name for event type
        var topicName = routingConfig.GetTopicName<TEvent>();


        // 3. Get or create sender for this topic
        var sender = senderCache.GetOrAdd(topicName,
            name => serviceBusClient.CreateSender(name));

        // 4. Serialize event to JSON
        var messageBody = JsonSerializer.Serialize(@event, JsonOptions.Default);

        // 5. Create Service Bus message
        var message = new ServiceBusMessage(messageBody)
        {
            MessageId = Guid.NewGuid().ToString(),
            Subject = @event.Name,
            ContentType = "application/json",
            ApplicationProperties =
            {
                ["EventType"] = typeof(TEvent).AssemblyQualifiedName,
                ["EventName"] = @event.Name,
                ["SequenceNo"] = @event.Metadata.SequenceNo
            }
        };

        // 6. Publish to Service Bus Topic
        await sender.SendMessageAsync(message);

        // 7. Log and telemetry
        logger.LogInformation(
            "Event published to Azure Service Bus: {Event} -> Topic: {Topic}, MessageId: {MessageId}",
            typeof(TEvent).Name, topicName, message.MessageId);

        telemetry.RecordAzureEventPublished(typeof(TEvent).Name, topicName);
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