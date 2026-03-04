using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Cloud.Azure.Messaging.Serialization;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Cloud.Azure.Messaging.Commands;

public class AzureServiceBusCommandListener : BackgroundService
{
    private readonly ServiceBusClient serviceBusClient;
    private readonly IServiceProvider serviceProvider;
    private readonly ICommandRoutingConfiguration routingConfig;
    private readonly ILogger<AzureServiceBusCommandListener> logger;
    private readonly List<ServiceBusProcessor> processors;

    public AzureServiceBusCommandListener(
        ServiceBusClient serviceBusClient,
        IServiceProvider serviceProvider,
        ICommandRoutingConfiguration routingConfig,
        ILogger<AzureServiceBusCommandListener> logger)
    {
        this.serviceBusClient = serviceBusClient;
        this.serviceProvider = serviceProvider;
        this.routingConfig = routingConfig;
        this.logger = logger;
        this.processors = new List<ServiceBusProcessor>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Get all queue names to listen to
        var queueNames = routingConfig.GetListeningQueues();

        // Create processor for each queue
        foreach (var queueName in queueNames)
        {
            var processor = serviceBusClient.CreateProcessor(queueName, new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = 10,
                AutoCompleteMessages = false, // Manual control
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5),
                ReceiveMode = ServiceBusReceiveMode.PeekLock
            });

            // Register message handler
            processor.ProcessMessageAsync += async args =>
            {
                await ProcessMessage(args, queueName, stoppingToken);
            };

            // Register error handler
            processor.ProcessErrorAsync += async args =>
            {
                logger.LogError(args.Exception,
                    "Error processing message from queue: {Queue}, Source: {Source}",
                    queueName, args.ErrorSource);
            };

            // Start processing
            await processor.StartProcessingAsync(stoppingToken);
            processors.Add(processor);

            logger.LogInformation("Started listening to Azure Service Bus queue: {Queue}", queueName);
        }

        // Wait for cancellation
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessMessage(
        ProcessMessageEventArgs args,
        string queueName,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = args.Message;

            // 1. Get command type from application properties
            var commandTypeName = message.ApplicationProperties["CommandType"] as string;
            var commandType = Type.GetType(commandTypeName);

            if (commandType == null)
            {
                logger.LogError("Unknown command type: {CommandType}", commandTypeName);
                await args.DeadLetterMessageAsync(message,
                    "UnknownCommandType",
                    $"Type not found: {commandTypeName}");
                return;
            }

            // 2. Deserialize command from message body
            var messageBody = args.Message.Body.ToString();
            var command = JsonSerializer.Deserialize(messageBody, commandType, JsonOptions.Default) as ICommand;

            if (command == null)
            {
                logger.LogError("Failed to deserialize command: {CommandType}", commandTypeName);
                await args.DeadLetterMessageAsync(message,
                    "DeserializationFailure",
                    "Failed to deserialize message body");
                return;
            }

            // 3. Create scoped service provider for command handling
            using var scope = serviceProvider.CreateScope();
            var commandSubscriber = scope.ServiceProvider
                .GetRequiredService<ICommandSubscriber>();

            // 4. Invoke Subscribe method using reflection (to preserve generics)
            var subscribeMethod = typeof(ICommandSubscriber)
                .GetMethod(nameof(ICommandSubscriber.Subscribe))
                .MakeGenericMethod(commandType);

            await (Task)subscribeMethod.Invoke(commandSubscriber, new[] { command });

            // 5. Complete the message (successful processing)
            await args.CompleteMessageAsync(message, cancellationToken);

            logger.LogInformation(
                "Command processed from Azure Service Bus: {Command}, Queue: {Queue}, MessageId: {MessageId}",
                commandType.Name, queueName, message.MessageId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error processing command from queue: {Queue}, MessageId: {MessageId}",
                queueName, args.Message.MessageId);

            // Let Service Bus retry or move to dead letter queue
            // Don't complete or abandon here - let auto-retry handle it
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop all processors gracefully
        foreach (var processor in processors)
        {
            await processor.StopProcessingAsync(cancellationToken);
            await processor.DisposeAsync();
        }
        processors.Clear();

        await base.StopAsync(cancellationToken);
    }
}
