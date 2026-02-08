using Microsoft.Extensions.Diagnostics.HealthChecks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using SourceFlow.Cloud.Azure.Configuration;

namespace SourceFlow.Cloud.Azure.Infrastructure;

public class AzureServiceBusHealthCheck : IHealthCheck
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IAzureCommandRoutingConfiguration _commandRoutingConfig;
    private readonly IAzureEventRoutingConfiguration _eventRoutingConfig;

    public AzureServiceBusHealthCheck(
        ServiceBusClient serviceBusClient,
        IAzureCommandRoutingConfiguration commandRoutingConfig,
        IAzureEventRoutingConfiguration eventRoutingConfig)
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

            // Test event topic subscriptions
            var eventSubscriptions = _eventRoutingConfig.GetListeningSubscriptions().Take(1).ToList();
            if (eventSubscriptions.Any())
            {
                var (topicName, subscriptionName) = eventSubscriptions.First();
                await using var receiver = _serviceBusClient.CreateReceiver(topicName, subscriptionName, new ServiceBusReceiverOptions
                {
                    ReceiveMode = ServiceBusReceiveMode.PeekLock
                });

                // Peek at messages (doesn't lock or remove them)
                await receiver.PeekMessageAsync(cancellationToken: cancellationToken);
                healthData["EventTopicStatus"] = "Accessible";
            }

            return HealthCheckResult.Healthy("Azure Service Bus is accessible", healthData);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Azure Service Bus is not accessible: {ex.Message}", ex);
        }
    }
}