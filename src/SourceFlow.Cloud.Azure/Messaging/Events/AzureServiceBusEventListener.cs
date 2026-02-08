using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Cloud.Azure.Configuration;
using SourceFlow.Cloud.Azure.Messaging.Serialization;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.Azure.Messaging.Events;

public class AzureServiceBusEventListener : BackgroundService
{
    private readonly ServiceBusClient serviceBusClient;
    private readonly IServiceProvider serviceProvider;
    private readonly IAzureEventRoutingConfiguration routingConfig;
    private readonly ILogger<AzureServiceBusEventListener> logger;
    private readonly List<ServiceBusProcessor> processors;

    public AzureServiceBusEventListener(
        ServiceBusClient serviceBusClient,
        IServiceProvider serviceProvider,
        IAzureEventRoutingConfiguration routingConfig,
        ILogger<AzureServiceBusEventListener> logger)
    {
        this.serviceBusClient = serviceBusClient;
        this.serviceProvider = serviceProvider;
        this.routingConfig = routingConfig;
        this.logger = logger;
        this.processors = new List<ServiceBusProcessor>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Get all topic subscriptions to listen to
        var subscriptions = routingConfig.GetListeningSubscriptions();

        // Create processor for each topic/subscription pair
        foreach (var (topicName, subscriptionName) in subscriptions)
        {
            var processor = serviceBusClient.CreateProcessor(
                topicName,
                subscriptionName,
                new ServiceBusProcessorOptions
                {
                    MaxConcurrentCalls = 20, // Higher for events (read-only)
                    AutoCompleteMessages = false,
                    MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5),
                    ReceiveMode = ServiceBusReceiveMode.PeekLock
                });

            // Register message handler
            processor.ProcessMessageAsync += async args =>
            {
                await ProcessMessage(args, topicName, subscriptionName, stoppingToken);
            };

            // Register error handler
            processor.ProcessErrorAsync += async args =>
            {
                logger.LogError(args.Exception,
                    "Error processing event from topic: {Topic}, subscription: {Subscription}",
                    topicName, subscriptionName);
            };

            // Start processing
            await processor.StartProcessingAsync(stoppingToken);
            processors.Add(processor);

            logger.LogInformation(
                "Started listening to Azure Service Bus topic: {Topic}, subscription: {Subscription}",
                topicName, subscriptionName);
        }

        // Wait for cancellation
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessMessage(
        ProcessMessageEventArgs args,
        string topicName,
        string subscriptionName,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = args.Message;

            // 1. Get event type from application properties
            var eventTypeName = message.ApplicationProperties["EventType"] as string;
            var eventType = Type.GetType(eventTypeName);

            if (eventType == null)
            {
                logger.LogError("Unknown event type: {EventType}", eventTypeName);
                await args.DeadLetterMessageAsync(message,
                    "UnknownEventType",
                    $"Type not found: {eventTypeName}");
                return;
            }

            // 2. Deserialize event from message body
            var messageBody = message.Body.ToString();
            var @event = JsonSerializer.Deserialize(messageBody, eventType, JsonOptions.Default) as IEvent;

            if (@event == null)
            {
                logger.LogError("Failed to deserialize event: {EventType}", eventTypeName);
                await args.DeadLetterMessageAsync(message,
                    "DeserializationFailure",
                    "Failed to deserialize message body");
                return;
            }

            // 3. Get event subscribers (singleton, so no scope needed)
            var eventSubscribers = serviceProvider.GetServices<IEventSubscriber>();

            // 4. Invoke Subscribe method for each subscriber
            var subscribeMethod = typeof(IEventSubscriber)
                .GetMethod(nameof(IEventSubscriber.Subscribe))
                .MakeGenericMethod(eventType);

            var tasks = eventSubscribers.Select(subscriber =>
                (Task)subscribeMethod.Invoke(subscriber, new[] { @event }));

            await Task.WhenAll(tasks);

            // 5. Complete the message
            await args.CompleteMessageAsync(message, cancellationToken);

            logger.LogInformation(
                "Event processed from Azure Service Bus: {Event}, Topic: {Topic}, MessageId: {MessageId}",
                eventType.Name, topicName, message.MessageId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error processing event from topic: {Topic}, subscription: {Subscription}, MessageId: {MessageId}",
                topicName, subscriptionName, args.Message.MessageId);

            // Let Service Bus retry or move to dead letter queue
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var processor in processors)
        {
            await processor.StopProcessingAsync(cancellationToken);
            await processor.DisposeAsync();
        }
        processors.Clear();

        await base.StopAsync(cancellationToken);
    }
}