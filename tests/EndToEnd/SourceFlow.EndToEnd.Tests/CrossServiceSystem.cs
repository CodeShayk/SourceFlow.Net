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
    private readonly Servers _servers;
    private readonly BrokerConnection _connection;

    private Action<SourceFlowBuilder>? _everyService;

    private CrossServiceSystem(Servers servers, Broker broker)
    {
        _servers = servers;
        _connection = new BrokerConnection(servers, broker, "e2e");
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
    public static Task<CrossServiceSystem> StartAsync(Servers servers, Broker broker, bool duplicateAccountsMessages = false)
    {
        return StartAsync(servers, broker, duplicateAccountsMessages ? inner => new DuplicatingTransport(inner) : null);
    }

    /// <summary>As <see cref="StartAsync(Servers, Broker, bool)"/>, with Accounts' transport wrapped by <paramref name="accountsTransport"/>.</summary>
    public static async Task<CrossServiceSystem> StartAsync(Servers servers, Broker broker, Func<ITransport, ITransport>? accountsTransport, Action<SourceFlowBuilder>? everyService = null)
    {
        await servers.PostgreSql.EnsureStartedAsync();
        await BrokerConnection.EnsureStartedAsync(servers, broker);

        var system = new CrossServiceSystem(servers, broker) { _everyService = everyService };
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
            }, accountsTransport);
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

    /// <summary>True when the nightly run puts this leg on the real cloud (<see cref="BrokerConnection.RealCloud"/>).</summary>
    public static bool RealCloud(Broker broker)
    {
        return BrokerConnection.RealCloud(broker);
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

        await _connection.DisposeAsync();
    }

    private async Task<IHost> StartServiceAsync(string service, string schema, Action<SourceFlowBuilder> configure, Func<ITransport, ITransport>? wrap = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = service;
            sf.AddModule<ContractsModule>();
            configure(sf);
            _everyService?.Invoke(sf);
            sf.UsePostgreSql(o =>
            {
                o.DataSource = _servers.PostgreSql.DataSource;
                o.Schema = schema;
            });
            sf.UseTransport(_ => wrap is null ? _connection.CreateTransport() : wrap(_connection.CreateTransport()));
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
}
