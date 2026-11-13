using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Inventory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orders;
using Payments;
using Shipping;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Testing;
using SourceFlow.Tool;
using SourceFlow.Transport;
using SourceFlow.Workflows;
using OrderId = Orders.OrderId;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>Where the services of an end-to-end system keep their data.</summary>
public enum Store
{
    InMemory,
    PostgreSql,
}

/// <summary>How an <see cref="OrderFulfilmentSystem"/> runs.</summary>
internal sealed record OrderFulfilmentOptions
{
    /// <summary>Every service sends and publishes every message twice: duplicate delivery everywhere (WF-E2E-11).</summary>
    public bool Duplicates { get; init; }

    /// <summary>How the orders service is added (default: the sample's <c>AddOrdersService()</c>).</summary>
    public Func<SourceFlowBuilder, SourceFlowBuilder> AddOrders { get; init; } = sf => sf.AddOrdersService();

    /// <summary>Faults injected into the first orders node (WF-E2E-12).</summary>
    public ChaosInjector? OrdersFaults { get; init; }
}

/// <summary>
/// The OrderFulfilment sample as four services (samples/OrderFulfilment), each with its own store (an in-memory store or a
/// PostgreSQL schema) and its own transport client on one broker, configured exactly as the sample's host configures
/// them (<c>AddOrdersService</c>, <c>AddInventoryService</c>, …). The orders service owns the order-fulfilment workflow.
/// </summary>
/// <remarks>
/// <para>
/// Time: the orders service runs on <see cref="Clock"/>, real time plus an offset the test moves forward, so the workflow's
/// timers (minutes to days) fall due at once while polling stays real. The other services run on the system clock.
/// </para>
/// <para>
/// The outside world: the payment gateway is <see cref="Gateway"/> (scripted outcomes per order, counted calls), the
/// warehouse is <see cref="Stock"/>, and <see cref="Outage(string)"/> takes a service's message handling down: what it
/// receives meanwhile is parked, as by an infrastructure failure the workflow only ever sees as a timeout.
/// </para>
/// </remarks>
internal sealed class OrderFulfilmentSystem : IAsyncDisposable
{
    public const string Workflow = "order-fulfilment";

    private readonly List<IHost> _hosts = [];
    private readonly Servers _servers;
    private readonly Store _store;
    private readonly BrokerConnection _connection;
    private readonly OrderFulfilmentOptions _options;
    private readonly Dictionary<string, string> _schemas = [];
    private readonly Dictionary<string, InMemoryStore> _memory = [];
    private readonly ConcurrentDictionary<string, bool> _outages = new(StringComparer.Ordinal);
    private int _ordersNodes;

    private OrderFulfilmentSystem(Servers servers, Store store, Broker broker, OrderFulfilmentOptions options)
    {
        _servers = servers;
        _store = store;
        _options = options;
        _connection = new BrokerConnection(servers, broker, "wf");
        foreach (string service in (string[])[OrdersService.Name, InventoryService.Name, PaymentsService.Name, ShippingService.Name])
        {
            _schemas[service] = PostgreSqlServer.NewSchema("wf_" + service);
            _memory[service] = new InMemoryStore(service == OrdersService.Name ? Clock : TimeProvider.System);
        }
    }

    /// <summary>The orders service's clock.</summary>
    public ShiftingClock Clock { get; } = new();

    /// <summary>The payment gateway the payments service charges through.</summary>
    public ScriptedGateway Gateway { get; } = new();

    /// <summary>The warehouse the inventory service asks.</summary>
    public TestStockLevels Stock { get; } = new();

    public IHost Orders { get; private set; } = null!;

    public IHost Inventory { get; private set; } = null!;

    public IHost Payments { get; private set; } = null!;

    public IHost Shipping { get; private set; } = null!;

    /// <summary>Deliveries per service and CloudEvents type, and how many of them the inbox found to be duplicates.</summary>
    public ConcurrentDictionary<(string Service, string Type), (int Deliveries, int Duplicates)> Received { get; } = new();

    public static async Task<OrderFulfilmentSystem> StartAsync(Servers servers, Store store, Broker broker, OrderFulfilmentOptions? options = null)
    {
        if (store == Store.PostgreSql)
        {
            await servers.PostgreSql.EnsureStartedAsync();
        }

        await BrokerConnection.EnsureStartedAsync(servers, broker);
        var system = new OrderFulfilmentSystem(servers, store, broker, options ?? new OrderFulfilmentOptions());
        try
        {
            // The owners first: their endpoints exist before the workflow sends to them.
            system.Inventory = await system.StartServiceAsync(InventoryService.Name, sf => sf.AddInventoryService());
            system.Payments = await system.StartServiceAsync(PaymentsService.Name, sf => sf.AddPaymentsService());
            system.Shipping = await system.StartServiceAsync(ShippingService.Name, sf => sf.AddShippingService());
            system.Orders = await system.StartOrdersAsync(system._options.AddOrders, system._options.OrdersFaults);
            return system;
        }
        catch
        {
            await system.DisposeAsync();
            throw;
        }
    }

    /// <summary>Starts another orders node on the same store and broker (a new deployment, or a replacement after a crash).</summary>
    public async Task<IHost> StartOrdersAsync(Func<SourceFlowBuilder, SourceFlowBuilder>? addOrders = null, ChaosInjector? faults = null)
    {
        IHost node = await StartServiceAsync(OrdersService.Name, addOrders ?? (sf => sf.AddOrdersService()), faults);
        Orders = node;
        return node;
    }

    /// <summary>Stops a node; with <paramref name="kill"/>, gives it a few seconds and abandons what it was doing.</summary>
    public async Task StopAsync(IHost host, bool kill = false)
    {
        _hosts.Remove(host);
        using var deadline = new CancellationTokenSource(kill ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30));
        try
        {
            await host.StopAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (kill)
        {
            // a killed process does not finish what it was doing
        }

        host.Dispose();
    }

    /// <summary>Takes a service's message handling down (true) or back up (false).</summary>
    public void Outage(string service, bool down = true)
    {
        _outages[service] = down;
    }

    // ================================================================ commands

    /// <summary>A deliverable address.</summary>
    public const string Address = "1 High Street, Springfield, SP1 2AB";

    /// <summary>Places an order, which starts its order-fulfilment workflow.</summary>
    public async Task PlaceAsync(OrderId order, decimal total, string[]? lines = null, string address = Address)
    {
        CommandResult result = await Commands.SendAsync(new PlaceOrder(order, lines ?? ["sku-teapot"], total, address), null, Ct);
        result.Status.ShouldBe(CommandStatus.Accepted);
    }

    /// <summary>Sends a command to the orders service (a reviewer's decision, a corrected address).</summary>
    public async Task SendAsync(ICommand command)
    {
        CommandResult result = await Commands.SendAsync(command, null, Ct);
        result.Status.ShouldBe(CommandStatus.Accepted);
    }

    public ICommandSender Commands => Orders.Services.GetRequiredService<ICommandSender>();

    /// <summary>Runs the SourceFlow CLI in the orders service (the operator's view).</summary>
    public async Task<(int Code, string Output, string Error)> CliAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await SourceFlowCli.RunAsync(args, Orders.Services, output, error, Ct);
        return (code, output.ToString(), error.ToString());
    }

    // ================================================================ reads

    /// <summary>The workflow-instances read model's row for an order, or null before the workflow started.</summary>
    public async Task<WorkflowInstanceSummary?> SummaryAsync(OrderId order, int version = 1)
    {
        StoredDocument? document = await OrdersRuntime.Storage.Documents.GetAsync(
            MessageEnvelope.DefaultTenant, WorkflowInstanceSummary.Collection, WorkflowInstanceSummary.DocumentId($"{Workflow}@{version}", order.ToString()), Ct);
        return document is null ? null : WorkflowRuntime.DeserializeSummary(document.Data.Span);
    }

    /// <summary>The engine's state of an order's instance (status, step, phase, attempt, stack, data).</summary>
    public async Task<JsonObject?> EngineStateAsync(OrderId order, int version = 1)
    {
        await using IStorageSession session = await OrdersRuntime.Storage.Sessions.BeginAsync(MessageEnvelope.DefaultTenant, Ct);
        ProcessState? state = await session.LoadProcessStateAsync($"{Workflow}@{version}", WorkflowInstanceId.From(order.ToString()).ToStreamSegment(), Ct);
        return state is null ? null : JsonNode.Parse(state.State.Span)?["state"] as JsonObject;
    }

    /// <summary>Waits until the order's instance satisfies <paramref name="condition"/>.</summary>
    public async Task<WorkflowInstanceSummary> WaitAsync(OrderId order, Func<WorkflowInstanceSummary, bool> condition, string what, int version = 1, TimeSpan? patience = null)
    {
        WorkflowInstanceSummary? last = null;
        await EventuallyAsync(async () => (last = await SummaryAsync(order, version)) is { } summary && condition(summary), what, patience,
            () => last is null ? "no instance" : $"{last.Status} at {last.Step} (attempt {last.Attempt}, failure {last.FailureCode ?? "none"})");
        return last!;
    }

    /// <summary>
    /// Waits until the engine waits in <paramref name="phase"/> at <paramref name="step"/> with <paramref name="attempt"/>
    /// (while <c>Retrying</c>, the attempt the retry will send), in <paramref name="status"/>.
    /// </summary>
    public Task WaitForPhaseAsync(OrderId order, string step, string phase, int? attempt = null, string? what = null, string status = "Running")
    {
        JsonObject? last = null;
        return EventuallyAsync(async () =>
        {
            last = await EngineStateAsync(order);
            return last is not null
                && (string?)last["status"] == status
                && (string?)last["step"] == step
                && string.Equals((string?)last["phase"], phase, StringComparison.OrdinalIgnoreCase)
                && (attempt is null || (int?)last["attempt"] == attempt);
        }, what ?? $"{step} {phase} attempt {attempt}", null, () => last?.ToJsonString() ?? "no state");
    }

    /// <summary>Moves the orders service's clock forward, so the timers due by then fire.</summary>
    public void Advance(TimeSpan by)
    {
        Clock.Advance(by);
    }

    /// <summary>The events of a stream in a service's own store, by CloudEvents type.</summary>
    public static async Task<IReadOnlyList<string>> EventTypesAsync(IHost service, StreamId stream)
    {
        SourceFlowRuntime runtime = service.Services.GetRequiredService<SourceFlowRuntime>();
        StreamSlice slice = await runtime.Storage.Events.ReadStreamAsync(runtime.Options.DefaultTenant, stream, new StreamVersion(1), int.MaxValue, Ct);
        return [.. slice.Events.Select(e => CloudEvents.FormatType(e.Type, e.TypeVersion))];
    }

    public Task<IReadOnlyList<string>> OrderEventsAsync(OrderId order) => EventTypesAsync(Orders, StreamId.For("order", order.ToStreamSegment()));

    public Task<IReadOnlyList<string>> ReservationEventsAsync(OrderId order) => EventTypesAsync(Inventory, StreamId.For("reservation", new Inventory.OrderId(order.Value).ToStreamSegment()));

    public Task<IReadOnlyList<string>> PaymentEventsAsync(OrderId order) => EventTypesAsync(Payments, StreamId.For("payment", new Payments.OrderId(order.Value).ToStreamSegment()));

    public Task<IReadOnlyList<string>> ShipmentEventsAsync(OrderId order) => EventTypesAsync(Shipping, StreamId.For("shipment", new Shipping.OrderId(order.Value).ToStreamSegment()));

    /// <summary>What each service parked, for a failure message.</summary>
    public async Task<string> DiagnoseAsync()
    {
        var text = new System.Text.StringBuilder();
        foreach (IHost host in _hosts)
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
        foreach (IHost host in Enumerable.Reverse(_hosts).ToList())
        {
            await StopAsync(host);
        }

        await _connection.DisposeAsync();
    }

    private SourceFlowRuntime OrdersRuntime => Orders.Services.GetRequiredService<SourceFlowRuntime>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task EventuallyAsync(Func<Task<bool>> condition, string what, TimeSpan? patience, Func<string> state)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + (patience ?? TimeSpan.FromSeconds(60));
        while (!await condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}. Last seen: {state()}. {await DiagnoseAsync()}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
        }
    }

    private async Task<IHost> StartServiceAsync(string service, Func<SourceFlowBuilder, SourceFlowBuilder> add, ChaosInjector? faults = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        bool orders = service == OrdersService.Name;
        builder.Services.AddSingleton(orders ? Clock : TimeProvider.System);
        builder.Services.AddSingleton<IStockLevels>(Stock);
        builder.Services.AddSingleton<IPaymentGateway>(Gateway);
        if (faults is not null)
        {
            builder.Services.AddSingleton<IFaultInjector>(faults);
        }

        int node = orders ? Interlocked.Increment(ref _ordersNodes) : 1;
        builder.Services.AddSourceFlow(sf =>
        {
            add(sf);
            if (_store == Store.PostgreSql)
            {
                sf.UsePostgreSql(o =>
                {
                    o.DataSource = _servers.PostgreSql.DataSource;
                    o.Schema = _schemas[service];
                });
            }
            else
            {
                sf.UseInMemoryStorage(_memory[service]);
            }

            sf.UseTransport(_ => _options.Duplicates ? new DuplicatingTransport(_connection.CreateTransport()) : _connection.CreateTransport());
            sf.UseMiddleware(_ => new ServiceMiddleware(this, service), PipelineStage.Receive);
            sf.Configure(o =>
            {
                o.NodeId = service + "-" + node.ToString(System.Globalization.CultureInfo.InvariantCulture);
                o.PollInterval = TimeSpan.FromMilliseconds(50);
                o.DeadLetterIngestInterval = TimeSpan.FromSeconds(1);
                o.LeaseTtl = ChaosSystem.LeaseTtl;
                o.ReceiveLockDuration = TimeSpan.FromSeconds(2);
            });
        });

        IHost host = builder.Build();
        try
        {
            await host.StartAsync(Ct);
        }
        catch
        {
            host.Dispose();
            throw;
        }

        _hosts.Add(host);
        return host;
    }

    /// <summary>Counts deliveries, and fails what a service receives while it is down (parked at once: an outage, not a bug).</summary>
    private sealed class ServiceMiddleware(OrderFulfilmentSystem system, string service) : IMessageMiddleware
    {
        public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            if (system._outages.GetValueOrDefault(service))
            {
                throw new PermanentException($"{service} is down.");
            }

            await next(context);
            bool duplicate = context.Outcome == HandlingOutcome.Duplicate;
            system.Received.AddOrUpdate(
                (service, CloudEvents.FormatType(context.Envelope.Type, context.Envelope.TypeVersion)),
                (1, duplicate ? 1 : 0),
                (_, n) => (n.Deliveries + 1, n.Duplicates + (duplicate ? 1 : 0)));
        }
    }
}

/// <summary>Real time plus an offset that only moves forward: timers fall due when the test says, polling stays real.</summary>
internal sealed class ShiftingClock : TimeProvider
{
    private long _offsetTicks;

    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;

    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        Interlocked.Add(ref _offsetTicks, by.Ticks);
    }
}

/// <summary>A payment gateway with scripted outcomes per order; unscripted charges are approved. Counts every call.</summary>
internal sealed class ScriptedGateway : IPaymentGateway
{
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<GatewayOutcome>> _script = new();
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();
    private readonly ConcurrentDictionary<Guid, int> _captures = new();
    private readonly ConcurrentDictionary<Guid, int> _refunds = new();

    /// <summary>The next charges of <paramref name="order"/> answer <paramref name="outcomes"/>, in order.</summary>
    public void Script(OrderId order, params GatewayOutcome[] outcomes)
    {
        ConcurrentQueue<GatewayOutcome> queue = _script.GetOrAdd(order.Value, _ => new ConcurrentQueue<GatewayOutcome>());
        foreach (GatewayOutcome outcome in outcomes)
        {
            queue.Enqueue(outcome);
        }
    }

    public int Attempts(OrderId order) => _attempts.GetValueOrDefault(order.Value);

    public int Captures(OrderId order) => _captures.GetValueOrDefault(order.Value);

    public int Refunds(Guid payment) => _refunds.GetValueOrDefault(payment);

    public ValueTask<GatewayOutcome> CaptureAsync(Guid order, decimal amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        _attempts.AddOrUpdate(order, 1, (_, n) => n + 1);
        GatewayOutcome outcome = _script.TryGetValue(order, out ConcurrentQueue<GatewayOutcome>? queue) && queue.TryDequeue(out GatewayOutcome next) ? next : GatewayOutcome.Approved;
        if (outcome == GatewayOutcome.Approved)
        {
            _captures.AddOrUpdate(order, 1, (_, n) => n + 1);
        }

        return ValueTask.FromResult(outcome);
    }

    public ValueTask RefundAsync(Guid payment, decimal amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        _refunds.AddOrUpdate(payment, 1, (_, n) => n + 1);
        return ValueTask.CompletedTask;
    }
}

/// <summary>A warehouse where everything is in stock except what the test takes out.</summary>
internal sealed class TestStockLevels : IStockLevels
{
    private readonly ConcurrentDictionary<string, bool> _out = new(StringComparer.OrdinalIgnoreCase);

    public void OutOfStock(string sku) => _out[sku] = true;

    public ValueTask<bool> IsAvailableAsync(string sku, CancellationToken cancellationToken) => ValueTask.FromResult(!_out.ContainsKey(sku));
}
