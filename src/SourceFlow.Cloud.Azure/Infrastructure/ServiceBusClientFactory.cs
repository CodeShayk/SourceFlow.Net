using Azure.Messaging.ServiceBus;
using Azure.Identity;

namespace SourceFlow.Cloud.Azure.Infrastructure;

public class ServiceBusClientFactory
{
    public static ServiceBusClient CreateWithConnectionString(string connectionString)
    {
        return new ServiceBusClient(connectionString, new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions
            {
                Mode = ServiceBusRetryMode.Exponential,
                MaxRetries = 3,
                Delay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromMinutes(1)
            },
            TransportType = ServiceBusTransportType.AmqpTcp
        });
    }

    public static ServiceBusClient CreateWithManagedIdentity(string fullyQualifiedNamespace)
    {
        return new ServiceBusClient(
            fullyQualifiedNamespace,
            new DefaultAzureCredential(),
            new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    Mode = ServiceBusRetryMode.Exponential,
                    MaxRetries = 3
                }
            });
    }
}
