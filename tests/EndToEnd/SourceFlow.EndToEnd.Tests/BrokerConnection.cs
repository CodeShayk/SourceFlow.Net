using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Grpc.Net.Client;
using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Azure;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Cloud.Tests.Aws;
using SourceFlow.Cloud.Tests.Azure;
using SourceFlow.Cloud.Tests.Gcp;
using SourceFlow.Messaging;
using SourceFlow.Transport;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>
/// One run's connection to a broker, shared by the services of an end-to-end system: a transport client per service on
/// the in-memory broker, an emulator, or (nightly) the real cloud. Physical names carry a per-run prefix, so runs on a
/// shared emulator don't collide, and disposal deletes every queue, topic and subscription with that prefix.
/// </summary>
internal sealed class BrokerConnection : IAsyncDisposable
{
    private readonly List<IDisposable> _resources = [];
    private readonly Servers _servers;
    private readonly Broker _broker;
    private readonly InMemoryBroker? _memory;
    private readonly string _prefix;

    public BrokerConnection(Servers servers, Broker broker, string prefix)
    {
        _servers = servers;
        _broker = broker;
        _memory = broker == Broker.InMemory ? new InMemoryBroker(TimeProvider.System) : null;
        _prefix = prefix + Guid.CreateVersion7().ToString("N")[^8..] + "-";
    }

    /// <summary>Starts the emulator <paramref name="broker"/> needs, unless the leg runs on the real cloud.</summary>
    public static async Task EnsureStartedAsync(Servers servers, Broker broker)
    {
        switch (RealCloud(broker) ? Broker.InMemory : broker)
        {
            case Broker.Aws:
                await servers.LocalStack.EnsureStartedAsync();
                break;
            case Broker.Gcp:
                await servers.PubSub.EnsureStartedAsync();
                break;
            case Broker.Azure:
                await servers.ServiceBus.EnsureStartedAsync();
                break;
        }
    }

    /// <summary>
    /// True when the nightly run (<c>SOURCEFLOW_REAL_CLOUD</c> = aws, gcp or azure) puts this leg on the real cloud instead of
    /// the emulator: AWS through the SDK credential chain and <c>AWS_REGION</c>, Google Cloud through Application Default
    /// Credentials and <c>GOOGLE_CLOUD_PROJECT</c>, Azure through <c>DefaultAzureCredential</c> and <c>AZURE_SERVICEBUS_NAMESPACE</c>.
    /// </summary>
    public static bool RealCloud(Broker broker)
    {
        string? cloud = Environment.GetEnvironmentVariable("SOURCEFLOW_REAL_CLOUD");
        return broker != Broker.InMemory && string.Equals(cloud, broker.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A new transport client on this run's broker (one per service host).</summary>
    public ITransport CreateTransport()
    {
        if (RealCloud(_broker))
        {
            return _broker switch
            {
                Broker.Aws => new AwsTransport(new AwsTransportOptions { Region = Environment.GetEnvironmentVariable("AWS_REGION"), NamePrefix = _prefix, ReceiveWaitTime = TimeSpan.FromSeconds(1) }),
                Broker.Gcp => new GcpTransport(new GcpTransportOptions
                {
                    ProjectId = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") ?? throw new InvalidOperationException("Set GOOGLE_CLOUD_PROJECT."),
                    EmulatorDetection = Google.Api.Gax.EmulatorDetection.ProductionOnly,
                    NamePrefix = _prefix,
                }),
                _ => new AzureTransport(new AzureTransportOptions
                {
                    FullyQualifiedNamespace = Environment.GetEnvironmentVariable("AZURE_SERVICEBUS_NAMESPACE") ?? throw new InvalidOperationException("Set AZURE_SERVICEBUS_NAMESPACE."),
                    NamePrefix = _prefix,
                }),
            };
        }

        switch (_broker)
        {
            case Broker.InMemory:
                return _memory!.CreateTransport();
            case Broker.Aws:
                var credentials = new BasicAWSCredentials("test", "test");
                string url = _servers.LocalStack.Endpoint.ToString();
                return new AwsTransport(
                    new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = url, AuthenticationRegion = LocalStackServer.Region }),
                    new AmazonSimpleNotificationServiceClient(credentials, new AmazonSimpleNotificationServiceConfig { ServiceURL = url, AuthenticationRegion = LocalStackServer.Region }),
                    new AwsTransportOptions { NamePrefix = _prefix, ReceiveWaitTime = TimeSpan.FromSeconds(1) });
            case Broker.Gcp:
                GrpcChannel channel = GrpcChannel.ForAddress("http://" + _servers.PubSub.Host, new GrpcChannelOptions { Credentials = ChannelCredentials.Insecure });
                _resources.Add(channel);
                CallInvoker invoker = channel.CreateCallInvoker();
                return new GcpTransport(
                    new PublisherServiceApiClientBuilder { CallInvoker = invoker }.Build(),
                    new SubscriberServiceApiClientBuilder { CallInvoker = invoker }.Build(),
                    new GcpTransportOptions { ProjectId = PubSubEmulatorServer.ProjectId, NamePrefix = _prefix });
            case Broker.Azure:
                return new AzureTransport(new AzureTransportOptions
                {
                    ConnectionString = ServiceBusEmulatorServer.ConnectionString(_servers.ServiceBus.Host, _servers.ServiceBus.AmqpPort),
                    AdministrationConnectionString = _servers.ServiceBus.AdministrationConnectionString,
                    NamePrefix = _prefix,
                    MessageTimeToLive = TimeSpan.FromHours(1),   // the emulator's maximum (.github/emulators/README.md)
                });
            default:
                throw new ArgumentOutOfRangeException(nameof(_broker), _broker, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DeleteResourcesAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best effort: emulators are discarded with the run; real-cloud leftovers carry the run's prefix.
        }

        foreach (IDisposable resource in _resources)
        {
            resource.Dispose();
        }
    }

    // Deletes every queue, topic and subscription of this run (they share the run's name prefix).
    private async Task DeleteResourcesAsync()
    {
        switch (_broker)
        {
            case Broker.Aws:
                using (var transport = (AwsTransport)CreateTransport())
                {
                    foreach (string url in (await transport.Sqs.ListQueuesAsync(new Amazon.SQS.Model.ListQueuesRequest { QueueNamePrefix = _prefix })).QueueUrls ?? [])
                    {
                        await transport.Sqs.DeleteQueueAsync(url);
                    }

                    string? next = null;
                    do
                    {
                        Amazon.SimpleNotificationService.Model.ListTopicsResponse page = await transport.Sns.ListTopicsAsync(new Amazon.SimpleNotificationService.Model.ListTopicsRequest { NextToken = next });
                        foreach (var topic in (page.Topics ?? []).Where(t => AwsNaming.NameOfArn(t.TopicArn).StartsWith(_prefix, StringComparison.Ordinal)))
                        {
                            await transport.Sns.DeleteTopicAsync(topic.TopicArn);
                        }

                        next = page.NextToken;
                    }
                    while (!string.IsNullOrEmpty(next));
                }

                break;
            case Broker.Gcp:
                var gcp = (GcpTransport)CreateTransport();
                string project = gcp.Options.ProjectId;
                foreach (Subscription subscription in gcp.Subscriber.ListSubscriptions(new Google.Api.Gax.ResourceNames.ProjectName(project)).Where(x => x.SubscriptionName.SubscriptionId.StartsWith(_prefix, StringComparison.Ordinal)))
                {
                    await gcp.Subscriber.DeleteSubscriptionAsync(subscription.SubscriptionName);
                }

                foreach (Topic topic in gcp.Publisher.ListTopics(new Google.Api.Gax.ResourceNames.ProjectName(project)).Where(x => x.TopicName.TopicId.StartsWith(_prefix, StringComparison.Ordinal)))
                {
                    await gcp.Publisher.DeleteTopicAsync(topic.TopicName);
                }

                break;
            case Broker.Azure:
                await using (var azure = (AzureTransport)CreateTransport())
                {
                    Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient admin = azure.Administration;
                    await foreach (Azure.Messaging.ServiceBus.Administration.TopicProperties topic in admin.GetTopicsAsync())
                    {
                        if (topic.Name.StartsWith(_prefix, StringComparison.Ordinal))
                        {
                            await admin.DeleteTopicAsync(topic.Name);
                        }
                    }

                    await foreach (Azure.Messaging.ServiceBus.Administration.QueueProperties queue in admin.GetQueuesAsync())
                    {
                        if (queue.Name.StartsWith(_prefix, StringComparison.Ordinal))
                        {
                            await admin.DeleteQueueAsync(queue.Name);
                        }
                    }
                }

                break;
        }
    }
}

/// <summary>Sends and publishes every message twice: duplicate delivery the receiving services must absorb (GAP-19).</summary>
internal sealed class DuplicatingTransport(ITransport inner) : ITransport, IAsyncDisposable
{
    public string SystemName => inner.SystemName;

    public TransportCapabilities Capabilities => inner.Capabilities;

    public async ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        await inner.SendAsync(endpoint, messages, cancellationToken);
        await inner.SendAsync(endpoint, messages, cancellationToken);
    }

    public async ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        await inner.PublishAsync(topic, messages, cancellationToken);
        await inner.PublishAsync(topic, messages, cancellationToken);
    }

    public ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken) => inner.CreateReceiverAsync(options, cancellationToken);

    public ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken) => inner.CreateDeadLetterReceiverAsync(endpoint, cancellationToken);

    public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken) => inner.ProvisionAsync(topology, mode, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        switch (inner)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
