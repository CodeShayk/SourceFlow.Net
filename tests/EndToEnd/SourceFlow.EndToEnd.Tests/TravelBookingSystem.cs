using System.Collections.Concurrent;
using Bookings;
using Hotels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Tool;
using SourceFlow.Workflows;
using BookingId = Bookings.BookingId;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>
/// The TravelBooking sample as its two services (samples/TravelBooking): Bookings, which owns the trip-booking workflow,
/// and Hotels, each with its own store (an in-memory store or a PostgreSQL schema) and its own transport client on one
/// broker, configured exactly as the sample's hosts configure them (<c>AddBookingsService</c>, <c>AddHotelsService</c>).
/// </summary>
/// <remarks>
/// Bookings runs on <see cref="Clock"/> (real time plus an offset the test moves forward), so the workflow's timers fall due
/// at once. <see cref="Outage(bool)"/> takes Hotels' message handling down: what it receives meanwhile is parked, and the
/// workflow only sees time pass.
/// </remarks>
internal sealed class TravelBookingSystem : IAsyncDisposable
{
    public const string Workflow = "trip-booking@1";

    private readonly List<IHost> _hosts = [];
    private readonly Servers _servers;
    private readonly Store _store;
    private readonly BrokerConnection _connection;
    private readonly Dictionary<string, string> _schemas = [];
    private readonly Dictionary<string, InMemoryStore> _memory = [];
    private readonly ConcurrentDictionary<string, bool> _outages = new(StringComparer.Ordinal);

    private TravelBookingSystem(Servers servers, Store store, Broker broker)
    {
        _servers = servers;
        _store = store;
        _connection = new BrokerConnection(servers, broker, "tb");
        foreach (string service in (string[])[BookingsService.Name, HotelsService.Name])
        {
            _schemas[service] = PostgreSqlServer.NewSchema("tb_" + service);
            _memory[service] = new InMemoryStore(service == BookingsService.Name ? Clock : TimeProvider.System);
        }
    }

    /// <summary>The Bookings service's clock.</summary>
    public ShiftingClock Clock { get; } = new();

    public IHost Bookings { get; private set; } = null!;

    public IHost Hotels { get; private set; } = null!;

    public static async Task<TravelBookingSystem> StartAsync(Servers servers, Store store, Broker broker)
    {
        if (store == Store.PostgreSql)
        {
            await servers.PostgreSql.EnsureStartedAsync();
        }

        await BrokerConnection.EnsureStartedAsync(servers, broker);
        var system = new TravelBookingSystem(servers, store, broker);
        try
        {
            // The owner of the commands first: its endpoint exists before the workflow sends to it.
            system.Hotels = await system.StartServiceAsync(HotelsService.Name, sf => sf.AddHotelsService());
            system.Bookings = await system.StartServiceAsync(BookingsService.Name, sf => sf.AddBookingsService());
            return system;
        }
        catch
        {
            await system.DisposeAsync();
            throw;
        }
    }

    /// <summary>Takes Hotels' message handling down (true) or back up (false).</summary>
    public void Outage(bool down = true)
    {
        _outages[HotelsService.Name] = down;
    }

    // ================================================================ commands

    /// <summary>Registers a hotel with its rooms.</summary>
    public async Task<HotelId> RegisterHotelAsync(int rooms, string name = "Seaview")
    {
        var hotel = HotelId.New();
        (await Send(Hotels, new RegisterHotel(hotel, name, rooms))).Status.ShouldBe(CommandStatus.Accepted);
        return hotel;
    }

    /// <summary>A guest requests a booking, which starts its trip-booking workflow.</summary>
    public async Task<BookingId> RequestAsync(HotelId hotel, int nights = 2)
    {
        var booking = BookingId.New();
        (await Send(Bookings, new RequestBooking(booking, hotel.Value, "Ada Lovelace", nights))).Status.ShouldBe(CommandStatus.Accepted);
        return booking;
    }

    /// <summary>Sends a command to the Bookings service (the guest).</summary>
    public async Task GuestAsync(ICommand command)
    {
        (await Send(Bookings, command)).Status.ShouldBe(CommandStatus.Accepted);
    }

    /// <summary>Sends a command to the Hotels service (hotel staff).</summary>
    public async Task StaffAsync(ICommand command)
    {
        (await Send(Hotels, command)).Status.ShouldBe(CommandStatus.Accepted);
    }

    /// <summary>Runs the SourceFlow CLI in the Bookings service (the operator's view).</summary>
    public async Task<(int Code, string Output, string Error)> CliAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await SourceFlowCli.RunAsync(args, Bookings.Services, output, error, Ct);
        return (code, output.ToString(), error.ToString());
    }

    // ================================================================ reads

    /// <summary>The workflow-instances read model's row for a booking, or null before the workflow started.</summary>
    public async Task<WorkflowInstanceSummary?> SummaryAsync(BookingId booking)
    {
        StoredDocument? document = await Runtime(Bookings).Storage.Documents.GetAsync(
            MessageEnvelope.DefaultTenant, WorkflowInstanceSummary.Collection, WorkflowInstanceSummary.DocumentId(Workflow, booking.ToString()), Ct);
        return document is null ? null : WorkflowRuntime.DeserializeSummary(document.Data.Span);
    }

    /// <summary>Waits until the booking's instance satisfies <paramref name="condition"/>.</summary>
    public async Task<WorkflowInstanceSummary> WaitAsync(BookingId booking, Func<WorkflowInstanceSummary, bool> condition, string what)
    {
        WorkflowInstanceSummary? last = null;
        await EventuallyAsync(async () => (last = await SummaryAsync(booking)) is { } summary && condition(summary), what,
            () => last is null ? "no instance" : $"{last.Status} at {last.Step} (attempt {last.Attempt}, failure {last.FailureCode ?? "none"})");
        return last!;
    }

    /// <summary>Waits until the booking's events in the Bookings service are <paramref name="expected"/>.</summary>
    public Task WaitForBookingEventsAsync(BookingId booking, params string[] expected)
    {
        IReadOnlyList<string> last = [];
        return EventuallyAsync(async () => (last = await BookingEventsAsync(booking)).SequenceEqual(expected),
            "booking events " + string.Join(", ", expected), () => string.Join(", ", last));
    }

    /// <summary>Waits until the hotel's rooms read model shows <paramref name="held"/> and <paramref name="confirmed"/>.</summary>
    public Task WaitForRoomsAsync(HotelId hotel, int held, int confirmed)
    {
        HotelRooms? last = null;
        return EventuallyAsync(async () => (last = await RoomsAsync(hotel)) is { } rooms && rooms.Held == held && rooms.Confirmed == confirmed,
            $"{held} held and {confirmed} confirmed", () => last is null ? "no document" : $"{last.Held} held, {last.Confirmed} confirmed");
    }

    /// <summary>The events of a booking's stream in the Bookings service, by CloudEvents type.</summary>
    public Task<IReadOnlyList<string>> BookingEventsAsync(BookingId booking) => EventTypesAsync(Bookings, StreamId.For(Booking.AggregateName, booking.ToStreamSegment()));

    /// <summary>The events of a hotel's stream in the Hotels service, by CloudEvents type.</summary>
    public Task<IReadOnlyList<string>> HotelEventsAsync(HotelId hotel) => EventTypesAsync(Hotels, StreamId.For(Hotel.AggregateName, hotel.ToStreamSegment()));

    /// <summary>A hotel's rooms read model.</summary>
    public async Task<HotelRooms?> RoomsAsync(HotelId hotel)
    {
        // The generation serving reads (blue/green read models, doc 06 §6).
        ReadModelGeneration generation = await Hotels.Services.GetRequiredService<IReadModelResolver>().ResolveAsync("hotel-rooms", Ct);
        StoredDocument? document = await Runtime(Hotels).Storage.Documents.GetAsync(MessageEnvelope.DefaultTenant, generation.Collection, hotel.ToStreamSegment(), Ct);
        return document is null ? null : (HotelRooms?)System.Text.Json.JsonSerializer.Deserialize(document.Data.Span, Runtime(Hotels).Model.JsonTypeInfoFor(typeof(HotelRooms)));
    }

    /// <summary>The CloudEvents types a service parked (dead letters).</summary>
    public static async Task<IReadOnlyList<string>> ParkedAsync(IHost service)
    {
        IReadOnlyList<DeadLetter> parked = await service.Services.GetRequiredService<IDeadLetterManager>().ListAsync(new DeadLetterQuery(), Ct);
        return [.. parked.Select(d => CloudEvents.FormatType(d.Envelope.Type, d.Envelope.TypeVersion))];
    }

    /// <summary>Moves the Bookings service's clock forward, so the timers due by then fire.</summary>
    public void Advance(TimeSpan by)
    {
        Clock.Advance(by);
    }

    /// <summary>What each service parked, for a failure message.</summary>
    public async Task<string> DiagnoseAsync()
    {
        var text = new System.Text.StringBuilder();
        foreach (IHost host in _hosts)
        {
            IReadOnlyList<DeadLetter> parked = await host.Services.GetRequiredService<IDeadLetterManager>().ListAsync(new DeadLetterQuery(), CancellationToken.None);
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"[{Runtime(host).ServiceName}: {parked.Count} dead letter(s)] ");
        }

        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IHost host in Enumerable.Reverse(_hosts).ToList())
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.StopAsync(deadline.Token);
            host.Dispose();
        }

        _hosts.Clear();
        await _connection.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SourceFlowRuntime Runtime(IHost host) => host.Services.GetRequiredService<SourceFlowRuntime>();

    private static ValueTask<CommandResult> Send(IHost host, ICommand command) => host.Services.GetRequiredService<ICommandSender>().SendAsync(command, null, Ct);

    private static async Task<IReadOnlyList<string>> EventTypesAsync(IHost service, StreamId stream)
    {
        SourceFlowRuntime runtime = Runtime(service);
        StreamSlice slice = await runtime.Storage.Events.ReadStreamAsync(runtime.Options.DefaultTenant, stream, new StreamVersion(1), int.MaxValue, Ct);
        return [.. slice.Events.Select(e => CloudEvents.FormatType(e.Type, e.TypeVersion))];
    }

    private async Task EventuallyAsync(Func<Task<bool>> condition, string what, Func<string> state)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(60);
        while (!await condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}. Last seen: {state()}. {await DiagnoseAsync()}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
        }
    }

    private async Task<IHost> StartServiceAsync(string service, Func<SourceFlowBuilder, SourceFlowBuilder> add)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSingleton(service == BookingsService.Name ? Clock : TimeProvider.System);
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

            sf.UseTransport(_ => _connection.CreateTransport());
            sf.UseMiddleware(_ => new OutageMiddleware(this, service), PipelineStage.Receive);
            sf.Configure(o =>
            {
                o.NodeId = service + "-1";
                o.PollInterval = TimeSpan.FromMilliseconds(50);
                o.DeadLetterIngestInterval = TimeSpan.FromSeconds(1);
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

    /// <summary>Fails what a service receives while it is down (parked at once: an outage, not a bug).</summary>
    private sealed class OutageMiddleware(TravelBookingSystem system, string service) : IMessageMiddleware
    {
        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            return system._outages.GetValueOrDefault(service) ? throw new PermanentException($"{service} is down.") : next(context);
        }
    }
}
