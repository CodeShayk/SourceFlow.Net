using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.Testing;

/// <summary>
/// Runs one SourceFlow service in memory for tests (doc 11 §2): in-memory store and broker, the real runtime, and
/// deterministic waits (<see cref="WaitForIdleAsync(TimeSpan?)"/>) instead of sleeps. Several hosts can share one broker
/// to test multi-service flows.
/// </summary>
public sealed class SourceFlowTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly SourceFlowHost _host;

    private SourceFlowTestHost(ServiceProvider provider, InMemoryStore store, InMemoryBroker broker, TimeProvider time)
    {
        _provider = provider;
        _host = provider.GetRequiredService<SourceFlowHost>();
        Store = store;
        Broker = broker;
        Time = time;
    }

    /// <summary>The service's DI container.</summary>
    public IServiceProvider Services => _provider;

    /// <summary>The service's in-memory store.</summary>
    public InMemoryStore Store { get; }

    /// <summary>The (possibly shared) broker.</summary>
    public InMemoryBroker Broker { get; }

    /// <summary>The clock (pass a fake one to control time).</summary>
    public TimeProvider Time { get; }

    /// <summary>The command sender.</summary>
    public ICommandSender Commands => _provider.GetRequiredService<ICommandSender>();

    /// <summary>The runtime context.</summary>
    public SourceFlowRuntime Runtime => _host.Runtime;

    /// <summary>The dead-letter manager.</summary>
    public IDeadLetterManager DeadLetters => _provider.GetRequiredService<IDeadLetterManager>();

    /// <summary>Starts a service. The store and broker are created unless given (share a broker across services).</summary>
    public static async Task<SourceFlowTestHost> StartAsync(
        Action<SourceFlowBuilder> configure,
        TimeProvider? time = null,
        InMemoryBroker? broker = null,
        InMemoryStore? store = null,
        Action<IServiceCollection>? services = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        time ??= TimeProvider.System;
        broker ??= new InMemoryBroker(time);
        store ??= new InMemoryStore(time);
        var collection = new ServiceCollection();
        collection.AddSingleton(time);
        services?.Invoke(collection);
        collection.AddSourceFlow(sf =>
        {
            sf.UseInMemoryStorage(store);
            sf.UseInMemoryTransport(broker);
            sf.Configure(o => o.PollInterval = TimeSpan.FromMilliseconds(50));
            configure(sf);
        });

        ServiceProvider provider = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var host = new SourceFlowTestHost(provider, store, broker, time);
        foreach (IHostedService hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        return host;
    }

    /// <summary>Sends a command.</summary>
    public ValueTask<CommandResult> SendAsync<TCommand>(TCommand command, SendOptions? options = null, CancellationToken cancellationToken = default)
        where TCommand : class, ICommand
    {
        return Commands.SendAsync(command, options, cancellationToken);
    }

    /// <summary>Waits until this host is idle: no due outbox rows, no active broker messages, nothing in flight, subscriptions caught up.</summary>
    public Task WaitForIdleAsync(TimeSpan? timeout = null)
    {
        return WaitForIdleAsync([this], timeout);
    }

    /// <summary>Waits until every host is idle (shared broker flows).</summary>
    public static async Task WaitForIdleAsync(IReadOnlyList<SourceFlowTestHost> hosts, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(30);
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + limit;
        int stable = 0;
        while (true)
        {
            bool idle = true;
            foreach (SourceFlowTestHost host in hosts)
            {
                idle &= await host.IsIdleAsync().ConfigureAwait(false);
            }

            if (idle && ++stable >= 3)
            {
                return;
            }

            if (!idle)
            {
                stable = 0;
            }

            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                string state = string.Join("; ", await Task.WhenAll(hosts.Select(h => h.DescribeAsync())).ConfigureAwait(false));
                throw new TimeoutException($"SourceFlow did not become idle within {limit}: {state}");
            }

            foreach (SourceFlowTestHost host in hosts)
            {
                host._host.Pulse();
                host.Broker.Pulse();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(2), TimeProvider.System).ConfigureAwait(false);
        }
    }

    /// <summary>Reads a projection document.</summary>
    public async Task<TDocument?> DocumentAsync<TDocument>(string collection, string id, string? tenant = null)
        where TDocument : class
    {
        StoredDocument? stored = await ((IDocumentReader)Store).GetAsync(tenant ?? Runtime.Options.DefaultTenant, collection, id, CancellationToken.None).ConfigureAwait(false);
        return stored is null ? null : (TDocument?)JsonSerializer.Deserialize(stored.Data.Span, Runtime.Model.JsonTypeInfoFor(typeof(TDocument)));
    }

    /// <summary>Reads the document of a projection registered with this service, from the generation serving reads (doc 06 §6).</summary>
    public async Task<TDocument?> DocumentAsync<TProjection, TDocument>(string id, string? tenant = null)
        where TDocument : class
    {
        ProjectionDescriptor projection = Runtime.Model.Projections.Single(p => p.ProjectionType == typeof(TProjection));
        string collection = await Runtime.Generations.ReadCollectionAsync(projection.Name, projection.Collection, CancellationToken.None).ConfigureAwait(false);
        return await DocumentAsync<TDocument>(collection, id, tenant).ConfigureAwait(false);
    }

    /// <summary>Reads a stream's events (deserialized and upcast).</summary>
    public async Task<IReadOnlyList<IEvent>> EventsAsync(StreamId stream, string? tenant = null)
    {
        StreamSlice slice = await Store.ReadStreamAsync(tenant ?? Runtime.Options.DefaultTenant, stream, new StreamVersion(1), int.MaxValue, CancellationToken.None).ConfigureAwait(false);
        return [.. slice.Events.Select(e =>
        {
            Messaging.MessageTypeInfo type = Runtime.Model.Registry.Get(e.Type, e.TypeVersion);
            object message = Messaging.MessageSerializerExtensions.Deserialize(Runtime.Serializer, e.Data, type);
            return (IEvent)Runtime.Model.Registry.UpcastToLatest(message, type, out _);
        })];
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (IHostedService hosted in _provider.GetServices<IHostedService>().Reverse())
        {
            await hosted.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await _provider.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<bool> IsIdleAsync()
    {
        return Store.DueOutboxCount() == 0
            && Broker.ActiveCount == 0
            && _host.ReceiveInFlight == 0
            && Runtime.Snapshots.Pending == 0
            && await _host.SubscriptionsCaughtUpAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<string> DescribeAsync()
    {
        bool caughtUp = await _host.SubscriptionsCaughtUpAsync(CancellationToken.None).ConfigureAwait(false);
        return $"{Runtime.ServiceName}: outbox due={Store.DueOutboxCount()}, broker active={Broker.ActiveCount}, receiving={_host.ReceiveInFlight}, snapshots pending={Runtime.Snapshots.Pending}, subscriptions caught up={caughtUp}";
    }
}
