using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using SourceFlow.Cloud.Azure;
using SourceFlow.Testing.Conformance;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Tests.Azure;

/// <summary>
/// The Service Bus transport as a TCK fixture: on the emulator (pull requests) behind a TCP proxy for real outages, with
/// throttling, duplicates and token refreshes injected at the client's senders; or on a real namespace (nightly,
/// <c>SOURCEFLOW_REAL_CLOUD=azure</c>) without faults. Each fixture uses its own name prefix and deletes its queues and
/// topics when disposed.
/// </summary>
internal sealed class AzureTransportFixture : ITransportFixture
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusAdministrationClient _admin;
    private readonly TcpProxy? _proxy;

    private AzureTransportFixture(ServiceBusClient client, ServiceBusAdministrationClient admin, AzureFaults? faults, AzureTransportOptions options, TcpProxy? proxy)
    {
        _client = client;
        _admin = admin;
        _proxy = proxy;
        Faults = faults;
        Transport = new AzureTransport(client, admin, options);
    }

    public ITransport Transport { get; }

    public AzureTransport Azure => (AzureTransport)Transport;

    public string Prefix { get; } = "tck-" + Guid.CreateVersion7().ToString("N")[^10..];

    public bool SupportsClockControl => false;

    public TimeSpan ReceiveTimeout => TimeSpan.FromSeconds(20);

    public TimeSpan QuietPeriod => TimeSpan.FromSeconds(1.5);

    public TimeSpan DelayTolerance => TimeSpan.FromSeconds(3);

    public ITransportFaults? Faults { get; }

    /// <summary>A fixture on the emulator, with fault injection. Queues get a 5-second lock and a 1-hour time to live (the emulator's limits).</summary>
    public static async Task<AzureTransportFixture> EmulatorAsync(ServiceBusEmulatorServer server)
    {
        await server.EnsureStartedAsync();
        var proxy = new TcpProxy(server.Host, server.AmqpPort);
        var faults = new AzureFaults(proxy);
        faults.Token();   // the first token is not a refresh
        var clientOptions = new ServiceBusClientOptions
        {
            // As in production: client retries honour the broker's back-off (the emulator throttles a busy namespace).
            // Injected faults sit above the client, so the transport's own reporting is what the TCK sees.
            RetryOptions = new ServiceBusRetryOptions { MaxRetries = 3, TryTimeout = TimeSpan.FromSeconds(10) },
        };
        var client = new FaultInjectingClient(new ServiceBusClient(ServiceBusEmulatorServer.ConnectionString("127.0.0.1", proxy.Port), clientOptions), faults);
        var options = new AzureTransportOptions { LockDuration = TimeSpan.FromSeconds(5), MessageTimeToLive = TimeSpan.FromHours(1) };
        return new AzureTransportFixture(client, new ServiceBusAdministrationClient(server.AdministrationConnectionString), faults, options, proxy);
    }

    /// <summary>A fixture on the namespace in <c>AZURE_SERVICEBUS_NAMESPACE</c> with <see cref="DefaultAzureCredential"/> (nightly).</summary>
    public static AzureTransportFixture RealCloud()
    {
        var options = new AzureTransportOptions
        {
            FullyQualifiedNamespace = Environment.GetEnvironmentVariable("AZURE_SERVICEBUS_NAMESPACE") ?? throw new InvalidOperationException("Set AZURE_SERVICEBUS_NAMESPACE."),
            LockDuration = TimeSpan.FromSeconds(5),
            MessageTimeToLive = TimeSpan.FromHours(1),
        };
        return new AzureTransportFixture(options.CreateClient(), options.CreateAdministrationClient(), null, options, null);
    }

    public ValueTask AdvanceClockAsync(TimeSpan by)
    {
        throw new NotSupportedException("Service Bus uses the real clock.");
    }

    public async ValueTask<bool> InjectDefectAsync(TopologyProblemKind defect, TransportTopology topology, CancellationToken cancellationToken)
    {
        string queue = AzureNaming.Queue("", topology.Endpoint);
        switch (defect)
        {
            case TopologyProblemKind.OrderingMismatch:
            {
                // Sessions are immutable: recreate the queue with the opposite setting.
                QueueProperties current = (await _admin.GetQueueAsync(queue, cancellationToken)).Value;
                await _admin.DeleteQueueAsync(queue, cancellationToken);
                await _admin.CreateQueueAsync(new CreateQueueOptions(queue)
                {
                    RequiresSession = !current.RequiresSession,
                    MaxDeliveryCount = current.MaxDeliveryCount,
                    LockDuration = current.LockDuration,
                    DefaultMessageTimeToLive = current.DefaultMessageTimeToLive,
                }, cancellationToken);
                return true;
            }

            case TopologyProblemKind.MissingDeadLetter:
            {
                // The dead-letter queue is built in; what can be wrong is the delivery count that leads to it.
                QueueProperties current = (await _admin.GetQueueAsync(queue, cancellationToken)).Value;
                current.MaxDeliveryCount = current.MaxDeliveryCount == 10 ? 9 : current.MaxDeliveryCount + 1;   // the emulator allows 1 to 10
                await _admin.UpdateQueueAsync(current, cancellationToken);
                return true;
            }

            case TopologyProblemKind.MissingSubscription:
                await _admin.DeleteSubscriptionAsync(AzureNaming.Topic("", topology.SubscribedTopics[0]), AzureNaming.Subscription("", topology.Endpoint), cancellationToken);
                return true;
            case TopologyProblemKind.FilterDrift:
            {
                string topic = AzureNaming.Topic("", topology.SubscribedTopics[0]);
                string subscription = AzureNaming.Subscription("", topology.Endpoint);
                var rules = new List<string>();
                await foreach (RuleProperties rule in _admin.GetRulesAsync(topic, subscription, cancellationToken))
                {
                    rules.Add(rule.Name);
                }

                await _admin.CreateRuleAsync(topic, subscription, new CreateRuleOptions("drifted", new CorrelationRuleFilter { ApplicationProperties = { ["cloudEvents:type"] = "drifted.v1" } }), cancellationToken);
                foreach (string rule in rules)
                {
                    await _admin.DeleteRuleAsync(topic, subscription, rule, cancellationToken);
                }

                return true;
            }

            default:
                return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Azure.DisposeAsync();
        try
        {
            await foreach (TopicProperties topic in _admin.GetTopicsAsync())
            {
                if (topic.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    await _admin.DeleteTopicAsync(topic.Name);
                }
            }

            await foreach (QueueProperties queue in _admin.GetQueuesAsync())
            {
                if (queue.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    await _admin.DeleteQueueAsync(queue.Name);
                }
            }
        }
        catch (Exception e) when (e is ServiceBusException or global::Azure.RequestFailedException)
        {
            // Best effort: the emulator is discarded with the run; real-cloud leftovers carry the tck- prefix.
        }
        finally
        {
            await _client.DisposeAsync();
            if (_proxy is not null)
            {
                await _proxy.DisposeAsync();
            }
        }
    }
}

/// <summary>Faults: outages in the TCP proxy; throttling, duplicates and token refreshes at the client's senders.</summary>
internal sealed class AzureFaults(TcpProxy proxy) : ITransportFaults
{
    private readonly object _gate = new();
    private int _throttled;
    private int _duplicates;
    private string? _token;
    private int _issued;

    public bool Unavailable
    {
        get => proxy.Unavailable;
        set => proxy.Unavailable = value;
    }

    public int CredentialRefreshes => Math.Max(0, Volatile.Read(ref _issued) - 1);

    public void ThrottleNext(int count, TimeSpan retryAfter)
    {
        Interlocked.Exchange(ref _throttled, count);
    }

    public void ExpireCredentials()
    {
        lock (_gate)
        {
            _token = null;
        }
    }

    public void DuplicateNext(int count)
    {
        Interlocked.Exchange(ref _duplicates, count);
    }

    /// <summary>
    /// The current token; an expired one is refreshed (and counted) on the next call. The emulator accepts shared keys
    /// only, so this stands in for the <c>TokenCredential</c> refresh the nightly run exercises against Entra ID.
    /// </summary>
    internal string Token()
    {
        lock (_gate)
        {
            return _token ??= "tck-token-" + Interlocked.Increment(ref _issued);
        }
    }

    internal bool ConsumeThrottle()
    {
        return Volatile.Read(ref _throttled) > 0 && Interlocked.Decrement(ref _throttled) >= 0;
    }

    internal bool ConsumeDuplicate()
    {
        return Volatile.Read(ref _duplicates) > 0 && Interlocked.Decrement(ref _duplicates) >= 0;
    }
}

/// <summary>A client whose senders inject the faults; everything else is the real client.</summary>
internal sealed class FaultInjectingClient(ServiceBusClient inner, AzureFaults faults) : ServiceBusClient
{
    public override string FullyQualifiedNamespace => inner.FullyQualifiedNamespace;

    public override bool IsClosed => inner.IsClosed;

    public override ServiceBusSender CreateSender(string queueOrTopicName)
    {
        return new FaultInjectingSender(inner.CreateSender(queueOrTopicName), faults);
    }

    public override ServiceBusReceiver CreateReceiver(string queueName, ServiceBusReceiverOptions options)
    {
        return inner.CreateReceiver(queueName, options);
    }

    public override Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(string queueName, ServiceBusSessionReceiverOptions options = default!, CancellationToken cancellationToken = default)
    {
        return inner.AcceptNextSessionAsync(queueName, options, cancellationToken);
    }

    public override ValueTask DisposeAsync()
    {
        return inner.DisposeAsync();
    }
}

/// <summary>A sender that throttles, duplicates and refreshes tokens on demand, around the real one.</summary>
internal sealed class FaultInjectingSender(ServiceBusSender inner, AzureFaults faults) : ServiceBusSender
{
    public override string EntityPath => inner.EntityPath;

    public override ValueTask<ServiceBusMessageBatch> CreateMessageBatchAsync(CancellationToken cancellationToken = default)
    {
        faults.Token();
        return inner.CreateMessageBatchAsync(cancellationToken);
    }

    public override async Task SendMessagesAsync(ServiceBusMessageBatch messageBatch, CancellationToken cancellationToken = default)
    {
        faults.Token();
        if (faults.ConsumeThrottle())
        {
            throw new ServiceBusException("Injected throttling: the namespace is busy.", ServiceBusFailureReason.ServiceBusy);
        }

        await inner.SendMessagesAsync(messageBatch, cancellationToken);
        if (faults.ConsumeDuplicate())
        {
            await inner.SendMessagesAsync(messageBatch, cancellationToken);   // the broker stores it twice
        }
    }

    public override Task<long> ScheduleMessageAsync(ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        faults.Token();
        return inner.ScheduleMessageAsync(message, scheduledEnqueueTime, cancellationToken);
    }

    public override ValueTask DisposeAsync()
    {
        return inner.DisposeAsync();
    }
}
