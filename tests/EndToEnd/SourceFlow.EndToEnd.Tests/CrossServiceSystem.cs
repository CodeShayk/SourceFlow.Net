using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Bank.Accounts;
using Bank.Contracts;
using Bank.Notifications;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Azure;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Cloud.Tests.Aws;
using SourceFlow.Cloud.Tests.Azure;
using SourceFlow.Cloud.Tests.Gcp;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Transport;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>The broker a cross-service scenario runs on.</summary>
public enum Broker
{
    InMemory,
    Aws,
    Gcp,
    Azure,
}

/// <summary>The emulators and PostgreSQL server, shared per test run (started on first use).</summary>
public sealed record Servers(PostgreSqlServer PostgreSql, LocalStackServer LocalStack, PubSubEmulatorServer PubSub, ServiceBusEmulatorServer ServiceBus);

/// <summary>
/// The Bank sample as separate services, each with its own PostgreSQL schema and its own transport client on one broker
/// (plan 06 §4, E2E-17..24): Accounts publishes <c>accounts.events</c> and routes <see cref="RequestStatement"/> to
/// Notifications; Notifications (two competing instances) and Loyalty each subscribe to <c>accounts.events</c> with their
/// own subscription. Physical names carry a per-run prefix, so runs on a shared emulator do not collide.
/// </summary>
internal sealed class CrossServiceSystem : IAsyncDisposable
{
    public const string Topic = "accounts.events";

    private readonly List<IHost> _hosts = [];
    private readonly List<IDisposable> _resources = [];
    private readonly Servers _servers;
    private readonly Broker _broker;
    private readonly InMemoryBroker? _memory;
    private readonly string _prefix = "e2e" + Guid.CreateVersion7().ToString("N")[^8..] + "-";

    private CrossServiceSystem(Servers servers, Broker broker)
    {
        _servers = servers;
        _broker = broker;
        _memory = broker == Broker.InMemory ? new InMemoryBroker(TimeProvider.System) : null;
    }

    public IHost Accounts { get; private set; } = null!;

    public IReadOnlyList<IHost> Notifications { get; private set; } = [];

    public IHost Loyalty { get; private set; } = null!;

    /// <summary>What every service received from the broker, duplicates included.</summary>
    public ReceiveTally Received { get; } = new();

    /// <summary>
    /// Starts the subscribers first (their subscriptions must exist before Accounts publishes), then Accounts. With
    /// <paramref name="duplicateAccountsMessages"/> every message Accounts sends or publishes reaches the broker twice.
    /// </summary>
    public static async Task<CrossServiceSystem> StartAsync(Servers servers, Broker broker, bool duplicateAccountsMessages = false)
    {
        await servers.PostgreSql.EnsureStartedAsync();
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

        var system = new CrossServiceSystem(servers, broker);
        try
        {
            string notifications = PostgreSqlServer.NewSchema("notifications");
            system.Notifications =
            [
                await system.StartServiceAsync("notifications", notifications, Subscriber),
                await system.StartServiceAsync("notifications", notifications, Subscriber),
            ];
            system.Loyalty = await system.StartServiceAsync("loyalty", PostgreSqlServer.NewSchema("loyalty"), Subscriber);
            system.Accounts = await system.StartServiceAsync("accounts", PostgreSqlServer.NewSchema("accounts"), sf =>
            {
                sf.AddModule<AccountsModule>();
                sf.AddModule<NotificationsCommandsModule>();
                sf.Routing(r =>
                {
                    r.Publish<AccountOpened>().As(AccountContracts.ToV1).To(Topic);
#pragma warning disable SF0014 // this compilation also holds Notifications; at runtime Accounts only routes the command (ADR-007)
                    r.Command<RequestStatement>().To("notifications");
#pragma warning restore SF0014
                });
            }, duplicateAccountsMessages);
            return system;
        }
        catch
        {
            await system.DisposeAsync();
            throw;
        }

        static void Subscriber(SourceFlowBuilder sf)
        {
            sf.AddModule<NotificationsModule>();
            sf.Routing(r => r.Subscribe(Topic));
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

    /// <summary>The events of a stream in a service's own store, with their CloudEvents type names.</summary>
    public static async Task<IReadOnlyList<(string Type, object Event)>> EventsAsync(IHost service, StreamId stream)
    {
        SourceFlowRuntime runtime = service.Services.GetRequiredService<SourceFlowRuntime>();
        StreamSlice slice = await runtime.Storage.Events.ReadStreamAsync(runtime.Options.DefaultTenant, stream, new StreamVersion(1), int.MaxValue, TestContext.Current.CancellationToken);
        return [.. slice.Events.Select(e =>
        {
            MessageTypeInfo type = runtime.Model.Registry.Get(e.Type, e.TypeVersion);
            return (e.Type, MessageSerializerExtensions.Deserialize(runtime.Serializer, e.Data, type));
        })];
    }

    /// <summary>The Notifications/Loyalty inbox stream of an account's customer.</summary>
    public static StreamId Inbox(AccountId account)
    {
        return StreamId.For(CustomerInbox.AggregateName, new CustomerId(account.Value));
    }

    /// <summary>What each service parked, for a failure message.</summary>
    public async Task<string> DiagnoseAsync()
    {
        var text = new System.Text.StringBuilder();
        foreach (IHost host in _hosts.Distinct())
        {
            SourceFlowRuntime runtime = host.Services.GetRequiredService<SourceFlowRuntime>();
            IReadOnlyList<DeadLetter> parked = await host.Services.GetRequiredService<IDeadLetterManager>().ListAsync(new DeadLetterQuery(), CancellationToken.None);
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"[{runtime.ServiceName}: {parked.Count} dead letter(s)");
            foreach (DeadLetter letter in parked.Take(3))
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"; {letter.Envelope.Type} at {letter.Consumer}: {letter.Reason}");
            }

            text.Append("] ");
        }

        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IHost host in Enumerable.Reverse(_hosts))
        {
            try
            {
                await host.StopAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                if (host is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else
                {
                    host.Dispose();
                }
            }
        }

        try
        {
            await DeleteResourcesAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best effort: emulators are discarded with the run; real-cloud leftovers carry the e2e prefix.
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

    private async Task<IHost> StartServiceAsync(string service, string schema, Action<SourceFlowBuilder> configure, bool duplicate = false)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = service;
            sf.AddModule<ContractsModule>();
            configure(sf);
            sf.UsePostgreSql(o =>
            {
                o.DataSource = _servers.PostgreSql.DataSource;
                o.Schema = schema;
            });
            sf.UseTransport(_ => duplicate ? new DuplicatingTransport(CreateTransport()) : CreateTransport());
            sf.UseMiddleware(_ => new TallyMiddleware(Received, service), PipelineStage.Receive);
            sf.Configure(o =>
            {
                o.PollInterval = TimeSpan.FromMilliseconds(50);
                o.DeadLetterIngestInterval = TimeSpan.FromSeconds(5);
            });
        });

        IHost host = builder.Build();
        _hosts.Add(host);
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private ITransport CreateTransport()
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

    /// <summary>Deliveries and inbox duplicates per service and CloudEvents type.</summary>
    public sealed class ReceiveTally
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Service, string Type, bool Duplicate), int> _counts = new();

        public int Deliveries(string service, string type) => _counts.GetValueOrDefault((service, type, false)) + _counts.GetValueOrDefault((service, type, true));

        public int Duplicates(string service, string type) => _counts.GetValueOrDefault((service, type, true));

        internal void Add(string service, string type, bool duplicate) => _counts.AddOrUpdate((service, type, duplicate), 1, (_, n) => n + 1);
    }

    private sealed class TallyMiddleware(ReceiveTally tally, string service) : IMessageMiddleware
    {
        public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            await next(context);
            tally.Add(service, CloudEvents.FormatType(context.Envelope.Type, context.Envelope.TypeVersion), context.Outcome == HandlingOutcome.Duplicate);
        }
    }

    /// <summary>Sends and publishes every message twice: duplicate delivery the receiving services must absorb (GAP-19).</summary>
    private sealed class DuplicatingTransport(ITransport inner) : ITransport, IAsyncDisposable
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
}
