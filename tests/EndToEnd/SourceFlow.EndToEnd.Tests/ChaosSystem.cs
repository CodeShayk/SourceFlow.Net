using System.Text.Json;
using Bank.Accounts;
using Bank.Contracts;
using Bank.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Testing;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>One running node of a service in a <see cref="ChaosSystem"/>.</summary>
internal sealed record ChaosNode(string Service, string Id, IHost Host, ChaosInjector? Faults)
{
    public ICommandSender Commands => Host.Services.GetRequiredService<ICommandSender>();

    public SourceFlowRuntime Runtime => Host.Services.GetRequiredService<SourceFlowRuntime>();
}

/// <summary>
/// Accounts and Notifications on PostgreSQL (one schema each) over one in-memory broker, with as many nodes per service as
/// a chaos scenario starts (plan 06 CH-01..10). Timings are short so failover happens in seconds: lease TTL 2 s, receive
/// lock 2 s, poll 50 ms. A node is killed by stopping it while its injector holds it dead: it writes nothing more, and
/// its leases and message locks expire as after a process kill.
/// </summary>
internal sealed class ChaosSystem : IAsyncDisposable
{
    public const string Topic = "accounts.events";

    public static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(2);

    private readonly List<ChaosNode> _nodes = [];
    private readonly NpgsqlDataSource _dataSource;
    private readonly IDisposable? _ownedDataSource;

    private ChaosSystem(NpgsqlDataSource dataSource, bool owned)
    {
        _dataSource = dataSource;
        _ownedDataSource = owned ? dataSource : null;
    }

    public InMemoryBroker Broker { get; } = new(TimeProvider.System);

    public string AccountsSchema { get; } = PostgreSqlServer.NewSchema("chaos_acc");

    public string NotificationsSchema { get; } = PostgreSqlServer.NewSchema("chaos_ntf");

    public NpgsqlDataSource DataSource => _dataSource;

    /// <summary>A system on the shared PostgreSQL server.</summary>
    public static ChaosSystem On(PostgreSqlServer server)
    {
        return new ChaosSystem(server.DataSource, owned: false);
    }

    /// <summary>A system on a database of its own, which a scenario may disconnect (CH-08).</summary>
    public static async Task<ChaosSystem> OnOwnDatabaseAsync(PostgreSqlServer server, string database)
    {
        await using (NpgsqlCommand create = server.DataSource.CreateCommand($"CREATE DATABASE \"{database}\""))
        {
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var builder = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = database, Timeout = 3, CommandTimeout = 10 };
        return new ChaosSystem(NpgsqlDataSource.Create(builder.ConnectionString), owned: true);
    }

    public Task<ChaosNode> StartAccountsAsync(string id, ChaosInjector? faults = null, Action<SourceFlowOptions>? options = null)
    {
        return StartAsync("accounts", id, AccountsSchema, faults, sf =>
        {
            sf.AddModule<AccountsModule>();
            sf.Projections(p => p.Add<AccountSummaryProjection>().Async());
            sf.Routing(r => r.Publish<AccountOpened>().As(AccountContracts.ToV1).To(Topic));
        }, options);
    }

    public Task<ChaosNode> StartNotificationsAsync(string id, ChaosInjector? faults = null)
    {
        return StartAsync("notifications", id, NotificationsSchema, faults, sf =>
        {
            sf.AddModule<NotificationsModule>();
            sf.Routing(r => r.Subscribe(Topic));
        }, null);
    }

    /// <summary>Kills a node: a stop that gives up after a few seconds, then disposal. In-flight work is abandoned.</summary>
    public async Task KillAsync(ChaosNode node)
    {
        _nodes.Remove(node);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await node.Host.StopAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // a killed process does not finish what it was doing
        }

        node.Host.Dispose();
    }

    // ================================================================ reads

    public async Task<AccountSummary?> SummaryAsync(ChaosNode accounts, AccountId id)
    {
        SourceFlowRuntime runtime = accounts.Runtime;
        ProjectionDescriptor projection = runtime.Model.Projections.Single(p => p.ProjectionType == typeof(AccountSummaryProjection));
        string collection = (await accounts.Host.Services.GetRequiredService<IReadModelResolver>().ResolveAsync(projection.Name, TestContext.Current.CancellationToken)).Collection;
        StoredDocument? stored = await runtime.Storage.Documents.GetAsync(MessageEnvelope.DefaultTenant, collection, id.ToStreamSegment(), TestContext.Current.CancellationToken);
        return stored is null ? null : (AccountSummary?)JsonSerializer.Deserialize(stored.Data.Span, runtime.Model.JsonTypeInfoFor(typeof(AccountSummary)));
    }

    /// <summary>How many welcome messages Notifications recorded for an account's customer (exactly one is correct).</summary>
    public async Task<int> WelcomesAsync(ChaosNode notifications, AccountId id)
    {
        StreamId inbox = StreamId.For(CustomerInbox.AggregateName, new CustomerId(id.Value));
        StreamSlice slice = await notifications.Runtime.Storage.Events.ReadStreamAsync(MessageEnvelope.DefaultTenant, inbox, new StreamVersion(1), int.MaxValue, TestContext.Current.CancellationToken);
        return slice.Events.Count(e => e.Type == "notifications.welcome.sent");
    }

    public async Task<long> PendingOutboxAsync()
    {
        await using var store = new PostgreSqlStore(new PostgreSqlStorageOptions { DataSource = _dataSource, Schema = AccountsSchema });
        return await store.CountAsync(TestContext.Current.CancellationToken);
    }

    public async Task<HealthReport> HealthAsync(ChaosNode node, string check)
    {
        return await node.Host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(c => c.Name == "sourceflow." + check, TestContext.Current.CancellationToken);
    }

    public static async Task EventuallyAsync(Func<Task<bool>> condition, string what, TimeSpan? patience = null)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + (patience ?? TimeSpan.FromSeconds(45));
        while (!await condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ChaosNode node in Enumerable.Reverse(_nodes).ToList())
        {
            await KillAsync(node);
        }

        if (_ownedDataSource is not null)
        {
            _ownedDataSource.Dispose();
        }
    }

    private async Task<ChaosNode> StartAsync(string service, string id, string schema, ChaosInjector? faults, Action<SourceFlowBuilder> configure, Action<SourceFlowOptions>? options)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSingleton(TimeProvider.System);
        if (faults is not null)
        {
            builder.Services.AddSingleton<IFaultInjector>(faults);
        }

        builder.Services.AddHealthChecks().AddSourceFlow();
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = service;
            sf.AddModule<ContractsModule>();
            configure(sf);
            sf.UsePostgreSql(o =>
            {
                o.DataSource = _dataSource;
                o.Schema = schema;
            });
            sf.UseTransport(_ => Broker.CreateTransport());
            sf.Configure(o =>
            {
                o.NodeId = service + "-" + id;
                o.PollInterval = TimeSpan.FromMilliseconds(50);
                o.LeaseTtl = LeaseTtl;
                o.ReceiveLockDuration = TimeSpan.FromSeconds(2);
                o.MonitorInterval = TimeSpan.FromMilliseconds(500);
                o.OutboxPartitions = 4;
                options?.Invoke(o);
            });
        });

        IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var node = new ChaosNode(service, id, host, faults);
        _nodes.Add(node);
        return node;
    }
}
