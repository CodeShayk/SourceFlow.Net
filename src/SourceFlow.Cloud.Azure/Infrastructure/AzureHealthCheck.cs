using Microsoft.Extensions.Diagnostics.HealthChecks;
using Azure.Messaging.ServiceBus;
using SourceFlow.Cloud.Configuration;

namespace SourceFlow.Cloud.Azure.Infrastructure;

public class AzureServiceBusHealthCheck : IHealthCheck
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ICommandRoutingConfiguration _commandRoutingConfig;
    private readonly IEventRoutingConfiguration _eventRoutingConfig;

    public AzureServiceBusHealthCheck(
        ServiceBusClient serviceBusClient,
        ICommandRoutingConfiguration commandRoutingConfig,
        IEventRoutingConfiguration eventRoutingConfig)
    {
        _serviceBusClient = serviceBusClient;
        _commandRoutingConfig = commandRoutingConfig;
        _eventRoutingConfig = eventRoutingConfig;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var healthData = new Dictionary<string, object>();

            // Test command queue connectivity
            var commandQueues = _commandRoutingConfig.GetListeningQueues().Take(1).ToList();
            if (commandQueues.Any())
            {
                var queueName = commandQueues.First();
                await using var receiver = _serviceBusClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
                {
                    ReceiveMode = ServiceBusReceiveMode.PeekLock
                });

                // Peek at messages (doesn't lock or remove them)
                await receiver.PeekMessageAsync(cancellationToken: cancellationToken);
                healthData["CommandQueueStatus"] = "Accessible";
            }

            // Test event queue connectivity (events are auto-forwarded to queues)
            var eventQueues = _eventRoutingConfig.GetListeningQueues().Take(1).ToList();
            if (eventQueues.Any())
            {
                var queueName = eventQueues.First();
                // Only check if not already checked as command queue
                if (!commandQueues.Contains(queueName))
                {
                    await using var receiver = _serviceBusClient.CreateReceiver(queueName, new ServiceBusReceiverOptions
                    {
                        ReceiveMode = ServiceBusReceiveMode.PeekLock
                    });

                    await receiver.PeekMessageAsync(cancellationToken: cancellationToken);
                }
                healthData["EventQueueStatus"] = "Accessible";
            }

            return HealthCheckResult.Healthy("Azure Service Bus is accessible", healthData);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Azure Service Bus is not accessible: {ex.Message}", ex);
        }
    }
}
