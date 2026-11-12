using System.Diagnostics.Metrics;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Diagnostics;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;
using SourceFlow.Transport;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-HC (plan 05, WP-4.3): every health check state in doc 08 §5, the monitor's gauges, and the instrument catalogue.</summary>
[Trait("Category", "Unit")]
public sealed class HealthCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ registration and readiness

    [Fact]
    [Trait("Id", "UT-HC-01")]
    [Trait("Covers", "doc 08 5")]
    public async Task EveryCheck_IsRegistered_WithReadinessTagsOnlyOnReadyStoreAndTransport()
    {
        await using SourceFlowTestHost host = await HostAsync();

        HealthReport report = await Health(host).CheckHealthAsync(Ct);

        report.Entries.Keys.ShouldBe(["sourceflow.ready", "sourceflow.store", "sourceflow.transport", "sourceflow.outbox", "sourceflow.subscriptions", "sourceflow.leases"], ignoreOrder: true);
        report.Status.ShouldBe(HealthStatus.Healthy, string.Join("; ", report.Entries.Select(e => $"{e.Key}: {e.Value.Description}")));
        report.Entries.Where(e => e.Value.Tags.Contains("ready")).Select(e => e.Key).ShouldBe(["sourceflow.ready", "sourceflow.store", "sourceflow.transport"], ignoreOrder: true);
        report.Entries.Values.ShouldAllBe(e => e.Tags.Contains("sourceflow"));
    }

    [Fact]
    [Trait("Id", "UT-HC-01")]
    public async Task Ready_IsUnhealthy_UntilTheHostStarted()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging().AddHealthChecks().AddSourceFlow();
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseInMemoryStorage();
        });
        await using ServiceProvider provider = services.BuildServiceProvider();

        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Name == "sourceflow.ready", Ct);

        report.Entries["sourceflow.ready"].Status.ShouldBe(HealthStatus.Unhealthy);
    }

    // ================================================================ store

    [Fact]
    [Trait("Id", "UT-HC-02")]
    public async Task Store_IsHealthy_WhenItAnswersQuickly_DegradedWhenSlow_UnhealthyWhenUnreachable()
    {
        var store = new InMemoryStore();
        await using SourceFlowTestHost host = await HostAsync(store: store, configure: sf => sf.Configure(o => o.Health.StoreLatencyDegraded = TimeSpan.FromMilliseconds(100)));

        HealthReportEntry healthy = await CheckAsync(host, SourceFlowHealthChecks.Store);
        store.Faults.DelayProbes(TimeSpan.FromMilliseconds(250));
        HealthReportEntry slow = await CheckAsync(host, SourceFlowHealthChecks.Store);
        store.Faults.DelayProbes(TimeSpan.Zero);
        store.Faults.FailProbes(new TimeoutException("connection refused"));
        HealthReportEntry down = await CheckAsync(host, SourceFlowHealthChecks.Store);

        healthy.Status.ShouldBe(HealthStatus.Healthy);
        healthy.Data.ShouldContainKey("latencyMs");
        slow.Status.ShouldBe(HealthStatus.Degraded);
        down.Status.ShouldBe(HealthStatus.Unhealthy);
        down.Description!.ShouldContain("connection refused");
    }

    [Fact]
    [Trait("Id", "UT-HC-03")]
    public async Task Store_WithoutAMonitor_IsCheckedThroughTheLog()
    {
        var store = new InMemoryStore();
        await using SourceFlowTestHost host = await HostAsync(configure: sf => sf.UseStorage(_ => new StoragePorts(store, store, store, store, store, store, store, store, store, store)));

        HealthReportEntry entry = await CheckAsync(host, SourceFlowHealthChecks.Store);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Data["detail"].ShouldBe("The log answered (the store reports no schema version).");
        (await CheckAsync(host, SourceFlowHealthChecks.Outbox)).Description!.ShouldContain("does not report");
    }

    // ================================================================ transport

    [Fact]
    [Trait("Id", "UT-HC-04")]
    public async Task Transport_IsHealthy_DegradedWhenThrottled_UnhealthyWhenUnreachable_AndCached()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        var broker = new InMemoryBroker(time);
        await using SourceFlowTestHost host = await HostAsync(time, broker: broker);

        HealthReportEntry healthy = await CheckAsync(host, SourceFlowHealthChecks.Transport);
        broker.Faults.Unavailable = true;
        HealthReportEntry cached = await CheckAsync(host, SourceFlowHealthChecks.Transport);
        time.Advance(TimeSpan.FromSeconds(31));
        HealthReportEntry down = await CheckAsync(host, SourceFlowHealthChecks.Transport);
        broker.Faults.Unavailable = false;
        broker.Faults.ThrottleNext(1, TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(31));
        HealthReportEntry throttled = await CheckAsync(host, SourceFlowHealthChecks.Transport);

        healthy.Status.ShouldBe(HealthStatus.Healthy);
        cached.Status.ShouldBe(HealthStatus.Healthy, "a result is reused for 30 s, so probes do not load the broker");
        down.Status.ShouldBe(HealthStatus.Unhealthy);
        throttled.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    [Trait("Id", "UT-HC-05")]
    public async Task Transport_WithoutAProbe_IsHealthy()
    {
        var transport = new UnprobedTransport(new InMemoryBroker().CreateTransport());
        await using SourceFlowTestHost host = await HostAsync(configure: sf => sf.UseTransport(_ => transport));

        HealthReportEntry entry = await CheckAsync(host, SourceFlowHealthChecks.Transport);

        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Description!.ShouldContain("does not support probing");
    }

    // ================================================================ outbox

    [Fact]
    [Trait("Id", "UT-HC-06")]
    [Trait("Covers", "doc 08 6")]
    public async Task Outbox_FollowsTheOldestDueMessage()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        await using SourceFlowTestHost host = await HostAsync(time, configure: sf =>
        {
            sf.Roles = SourceFlowRoles.Api;   // no relay: the message stays in the outbox
            sf.Routing(r => r.Publish<AccountOpened>().AsIs().To("accounts.events"));
        });

        HealthReportEntry empty = await CheckAsync(host, SourceFlowHealthChecks.Outbox);
        await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 1m), cancellationToken: Ct);
        await Monitor(host).SampleAsync(Ct);
        HealthReportEntry fresh = await CheckAsync(host, SourceFlowHealthChecks.Outbox);
        time.Advance(TimeSpan.FromSeconds(11));
        await Monitor(host).SampleAsync(Ct);
        HealthReportEntry late = await CheckAsync(host, SourceFlowHealthChecks.Outbox);
        time.Advance(TimeSpan.FromMinutes(5));
        await Monitor(host).SampleAsync(Ct);
        HealthReportEntry stuck = await CheckAsync(host, SourceFlowHealthChecks.Outbox);

        empty.Status.ShouldBe(HealthStatus.Healthy);
        fresh.Status.ShouldBe(HealthStatus.Healthy);
        late.Status.ShouldBe(HealthStatus.Degraded);
        stuck.Status.ShouldBe(HealthStatus.Unhealthy);
        stuck.Data["pending"].ShouldBe(1);
    }

    // ================================================================ subscriptions

    [Fact]
    [Trait("Id", "UT-HC-07")]
    [Trait("Covers", "doc 08 6")]
    public async Task Subscriptions_AreDegraded_WhenAPartitionLags_AndHealthyOnceCaughtUp()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        await using SourceFlowTestHost host = await DripsAsync(time);
        await host.SendAsync(new Drip(DripId.New(), "one"), cancellationToken: Ct);
        await host.SendAsync(new Drip(DripId.New(), "two"), cancellationToken: Ct);

        await Monitor(host).SampleAsync(Ct);
        HealthReportEntry fresh = await CheckAsync(host, SourceFlowHealthChecks.Subscriptions);
        time.Advance(TimeSpan.FromSeconds(31));
        await Monitor(host).SampleAsync(Ct);
        HealthReportEntry lagging = await CheckAsync(host, SourceFlowHealthChecks.Subscriptions);
        await Engine(host).RunOnceAsync(Ct);
        await Monitor(host).SampleAsync(Ct);
        HealthReportEntry current = await CheckAsync(host, SourceFlowHealthChecks.Subscriptions);

        fresh.Status.ShouldBe(HealthStatus.Healthy);
        lagging.Status.ShouldBe(HealthStatus.Degraded);
        lagging.Description!.ShouldContain("behind");
        current.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    [Trait("Id", "UT-HC-08")]
    [Trait("Covers", "doc 06 7")]
    public async Task Subscriptions_AreDegraded_WhenStalled_AndUnhealthyAfterFiveMinutes()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        await using SourceFlowTestHost host = await DripsAsync(time, SubscriptionFailurePolicy.Stop);
        await host.SendAsync(new Drip(DripId.New(), "poison"), cancellationToken: Ct);
        await Engine(host).RunOnceAsync(Ct);

        await Monitor(host).SampleAsync(Ct);
        HealthReportEntry stalled = await CheckAsync(host, SourceFlowHealthChecks.Subscriptions);
        time.Advance(TimeSpan.FromSeconds(2));
        await Engine(host).RunOnceAsync(Ct);   // a retry keeps the original stall time
        time.Advance(TimeSpan.FromMinutes(5));
        await Monitor(host).SampleAsync(Ct);
        HealthReportEntry halted = await CheckAsync(host, SourceFlowHealthChecks.Subscriptions);

        stalled.Status.ShouldBe(HealthStatus.Degraded);
        stalled.Description!.ShouldContain("stalled");
        halted.Status.ShouldBe(HealthStatus.Unhealthy);
        halted.Data["stalled"].ShouldBe(1);
    }

    // ================================================================ leases

    [Fact]
    [Trait("Id", "UT-HC-09")]
    public async Task Leases_AreHealthy_WithNothingToOwn_OrTheFairShare_AndDegradedWhenUnderShare()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        var store = new InMemoryStore(time);
        await using SourceFlowTestHost api = await HostAsync(configure: sf => sf.Roles = SourceFlowRoles.Api);
        await using SourceFlowTestHost host = await DripsAsync(time, store: store, configure: sf =>
        {
            sf.Roles = SourceFlowRoles.All;   // the subscription loop takes the lease at start, then waits (no commits, long poll)
            sf.Configure(o =>
            {
                o.PollInterval = TimeSpan.FromMinutes(10);
                o.LeaseTtl = TimeSpan.FromMinutes(20);
            });
        });

        HealthReportEntry none = await CheckAsync(api, SourceFlowHealthChecks.Leases);
        await Engine(host).RunOnceAsync(Ct);
        await Monitor(host).SampleAsync(Ct);
        HealthReportEntry owned = await CheckAsync(host, SourceFlowHealthChecks.Leases);

        // Another owner takes the partition after this node's lease expired: it stays under its share.
        time.Advance(TimeSpan.FromMinutes(21));
        (await store.TryAcquireAsync("sub:" + Drops + ":0", "another-node", TimeSpan.FromHours(1), Ct)).ShouldNotBeNull();
        await Engine(host).RunOnceAsync(Ct);
        await Monitor(host).SampleAsync(Ct);
        HealthReportEntry briefly = await CheckAsync(host, SourceFlowHealthChecks.Leases);
        time.Advance(TimeSpan.FromSeconds(21));
        await Monitor(host).SampleAsync(Ct);
        HealthReportEntry under = await CheckAsync(host, SourceFlowHealthChecks.Leases);

        none.Status.ShouldBe(HealthStatus.Healthy);
        none.Description!.ShouldContain("does not run subscriptions");
        owned.Status.ShouldBe(HealthStatus.Healthy);
        owned.Data["owned"].ShouldBe(1);
        briefly.Status.ShouldBe(HealthStatus.Healthy, "under share for less than two heartbeat intervals");
        under.Status.ShouldBe(HealthStatus.Degraded);
    }

    // ================================================================ gauges and catalogue

    [Fact]
    [Trait("Id", "UT-HC-10")]
    [Trait("Covers", "doc 08 3.2")]
    public async Task TheGauges_ReportTheLatestSample()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        await using SourceFlowTestHost host = await DripsAsync(time, configure: sf => sf.Routing(r => r.Publish<Dripped>().AsIs().To("drips.events")));
        await host.SendAsync(new Drip(DripId.New(), "one"), cancellationToken: Ct);
        await host.SendAsync(new Drip(DripId.New(), "two"), cancellationToken: Ct);
        await host.Store.ParkAsync(new DeadLetter
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            Consumer = "drips",
            Source = "test",
            Envelope = (await host.Store.ReadStreamAsync(MessageEnvelope.DefaultTenant, (await host.Store.ReadAsync(GlobalPosition.Start, BucketRange.All, 1, Ct)).Events[0].StreamId, new StreamVersion(1), 1, Ct)).Events[0].ToEnvelope(),
            Reason = "test",
            Attempts = 1,
            FirstFailedAt = time.GetUtcNow(),
            ParkedAt = time.GetUtcNow(),
        }, Ct);
        time.Advance(TimeSpan.FromSeconds(12));
        await Monitor(host).SampleAsync(Ct);

        Dictionary<string, double> gauges = Gauges(host);

        gauges[SourceFlowTelemetry.Instruments.OutboxPending].ShouldBe(2);
        gauges[SourceFlowTelemetry.Instruments.OutboxAge].ShouldBe(12, 0.5);
        gauges[SourceFlowTelemetry.Instruments.DeadLetterCount].ShouldBe(1);
        gauges[SourceFlowTelemetry.Instruments.SubscriptionLag].ShouldBe(2);
        gauges[SourceFlowTelemetry.Instruments.SubscriptionLagTime].ShouldBe(12, 0.5);
        gauges[SourceFlowTelemetry.Instruments.HorizonAge].ShouldBe(0);
        gauges[SourceFlowTelemetry.Instruments.LeaseOwned].ShouldBe(0);
        gauges.ShouldNotContainKey(SourceFlowTelemetry.Instruments.ProcessActive, "no process manager in this service");
    }

    [Fact]
    [Trait("Id", "UT-HC-11")]
    [Trait("Covers", "doc 08 3.2;GAP-14")]
    public async Task TheCatalogue_IsExactlyWhatAHostPublishes_AndRecordedTagsAreDeclared()
    {
        var published = new Dictionary<string, Instrument>(StringComparer.Ordinal);
        var recorded = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        SourceFlowMetrics? metrics = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SourceFlowTelemetry.MeterName && (metrics is null || ReferenceEquals(instrument.Meter, metrics.Meter)))
                {
                    lock (published)
                    {
                        published[instrument.Name] = instrument;
                    }

                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (recorded)
            {
                HashSet<string> keys = recorded.TryGetValue(instrument.Name, out HashSet<string>? existing) ? existing : recorded[instrument.Name] = new HashSet<string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    keys.Add(tag.Key);
                }
            }
        }

        listener.SetMeasurementEventCallback<long>((i, _, t, _) => Record(i, t));
        listener.SetMeasurementEventCallback<int>((i, _, t, _) => Record(i, t));
        listener.SetMeasurementEventCallback<double>((i, _, t, _) => Record(i, t));
        listener.Start();

        await using SourceFlowTestHost host = await DripsAsync(new FakeTimeProvider(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero)), SubscriptionFailurePolicy.Park);
        metrics = host.Services.GetRequiredService<SourceFlowMetrics>();
        await host.SendAsync(new Drip(DripId.New(), "one"), cancellationToken: Ct);
        await host.SendAsync(new Drip(DripId.New(), "poison"), cancellationToken: Ct);
        await Engine(host).RunOnceAsync(Ct);
        await Monitor(host).SampleAsync(Ct);
        listener.RecordObservableInstruments();

        Dictionary<string, InstrumentInfo> catalogue = SourceFlowTelemetry.Catalogue.ToDictionary(i => i.Name, StringComparer.Ordinal);
        published.Keys.Order(StringComparer.Ordinal).ShouldBe(catalogue.Keys.Order(StringComparer.Ordinal), "every instrument is in the catalogue, and the catalogue lists nothing else");
        foreach ((string name, Instrument instrument) in published)
        {
            InstrumentInfo info = catalogue[name];
            instrument.Unit.ShouldBe(info.Unit, name);
            instrument.Description.ShouldBe(info.Description, name);
            Kind(instrument).ShouldBe(info.Kind, name);
        }

        recorded.Keys.ShouldContain(SourceFlowTelemetry.Instruments.CommandDuration);
        recorded.Keys.ShouldContain(SourceFlowTelemetry.Instruments.DeadLetterParked);
        foreach ((string name, HashSet<string> keys) in recorded)
        {
            keys.ShouldBeSubsetOf(catalogue[name].Tags, $"{name} records only the tags its catalogue entry declares");
        }
    }

    [Fact]
    [Trait("Id", "UT-HC-12")]
    public void HealthThresholds_AreValidated()
    {
        var options = new SourceFlowOptions { ServiceName = "accounts" };
        options.Health.OutboxAgeUnhealthy = TimeSpan.FromSeconds(5);
        options.Health.UnderShareIntervals = 0;
        options.Health.LagTimeDegraded = TimeSpan.Zero;

        ValidateOptionsResult result = new SourceFlowOptionsTimingValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("OutboxAgeUnhealthy");
        result.FailureMessage.ShouldContain("UnderShareIntervals");
        result.FailureMessage.ShouldContain("LagTimeDegraded");
    }

    [Fact]
    [Trait("Id", "UT-HC-13")]
    public async Task AFailingReading_DoesNotStopTheOthers()
    {
        var store = new InMemoryStore();
        var ports = new StoragePorts(store, store, store, store, store, store, store, store, store, store) { Monitor = new BrokenOutboxMonitor(store) };
        await using SourceFlowTestHost host = await HostAsync(configure: sf => sf.UseStorage(_ => ports));

        MonitorSnapshot snapshot = await Monitor(host).SampleAsync(Ct);
        HealthReportEntry outbox = await CheckAsync(host, SourceFlowHealthChecks.Outbox);

        snapshot.Outbox.ShouldBeNull();
        snapshot.DeadLetters.ShouldNotBeNull();
        snapshot.Errors.ShouldHaveSingleItem().ShouldContain("outbox");
        outbox.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    [Trait("Id", "UT-HC-14")]
    public async Task TheMonitor_RunsWithTheHost_AndKeepsItsSampleFresh()
    {
        await using SourceFlowTestHost host = await HostAsync(configure: sf => sf.Configure(o => o.MonitorInterval = TimeSpan.FromMilliseconds(100)));
        SourceFlowMonitor monitor = Monitor(host);

        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (monitor.Latest is null && TimeProvider.System.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct);
        }

        MonitorSnapshot first = monitor.Latest.ShouldNotBeNull("sampled at start");
        await Task.Delay(TimeSpan.FromMilliseconds(400), TimeProvider.System, Ct);
        monitor.Latest!.ShouldNotBeSameAs(first, "sampled again on the interval");
    }

    // ================================================================ helpers

    private const string Drops = "projection:test-drips@1";

    private static Task<SourceFlowTestHost> HostAsync(TimeProvider? time = null, InMemoryBroker? broker = null, InMemoryStore? store = null, Action<SourceFlowBuilder>? configure = null)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            configure?.Invoke(sf);
        }, time, broker, store, services: s => s.AddLogging().AddHealthChecks().AddSourceFlow(), cancellationToken: Ct);
    }

    private static Task<SourceFlowTestHost> DripsAsync(TimeProvider time, SubscriptionFailurePolicy policy = SubscriptionFailurePolicy.Park, InMemoryStore? store = null, Action<SourceFlowBuilder>? configure = null)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "drips";
            sf.AddModule<CoreTestModule>();
            sf.Roles = SourceFlowRoles.Api;   // no background loops: the test runs the engine
            sf.Projections(p => p.Add<DripsProjection>().OnFailure(policy));
            configure?.Invoke(sf);
        }, time, store: store, services: s => s.AddLogging().AddHealthChecks().AddSourceFlow(), cancellationToken: Ct);
    }

    private static HealthCheckService Health(SourceFlowTestHost host)
    {
        return host.Services.GetRequiredService<HealthCheckService>();
    }

    private static async Task<HealthReportEntry> CheckAsync(SourceFlowTestHost host, string check)
    {
        string name = "sourceflow." + check;
        HealthReport report = await Health(host).CheckHealthAsync(c => c.Name == name, Ct);
        return report.Entries[name];
    }

    private static SourceFlowMonitor Monitor(SourceFlowTestHost host)
    {
        return host.Services.GetRequiredService<SourceFlowMonitor>();
    }

    private static SubscriptionEngine Engine(SourceFlowTestHost host)
    {
        return host.Services.GetRequiredService<SubscriptionEngine>();
    }

    private static Dictionary<string, double> Gauges(SourceFlowTestHost host)
    {
        Meter meter = host.Services.GetRequiredService<SourceFlowMetrics>().Meter;
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.GetType().Name.StartsWith("ObservableGauge", StringComparison.Ordinal))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        // Counts add up over their tags (partitions, consumers); ages (unit s) take the oldest.
        void Add(Instrument i, double v)
        {
            values[i.Name] = i.Unit == "s" ? Math.Max(values.GetValueOrDefault(i.Name), v) : values.GetValueOrDefault(i.Name) + v;
        }

        listener.SetMeasurementEventCallback<int>((i, v, _, _) => Add(i, v));
        listener.SetMeasurementEventCallback<long>((i, v, _, _) => Add(i, v));
        listener.SetMeasurementEventCallback<double>((i, v, _, _) => Add(i, v));
        listener.Start();
        listener.RecordObservableInstruments();
        return values;
    }

    private static InstrumentKind Kind(Instrument instrument)
    {
        string type = instrument.GetType().Name;
        return type.StartsWith("Counter", StringComparison.Ordinal) ? InstrumentKind.Counter
            : type.StartsWith("Histogram", StringComparison.Ordinal) ? InstrumentKind.Histogram
            : type.StartsWith("ObservableGauge", StringComparison.Ordinal) ? InstrumentKind.Gauge
            : throw new InvalidOperationException($"Unexpected instrument type {type} for {instrument.Name}.");
    }

    private sealed class BrokenOutboxMonitor(InMemoryStore inner) : IStoreMonitor
    {
        public ValueTask<StoreProbe> ProbeAsync(CancellationToken cancellationToken) => inner.ProbeAsync(cancellationToken);

        public ValueTask<IReadOnlyList<OutboxBacklog>> GetOutboxBacklogAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("outbox table locked");

        public ValueTask<IReadOnlyDictionary<string, int>> CountParkedDeadLettersAsync(CancellationToken cancellationToken) => inner.CountParkedDeadLettersAsync(cancellationToken);

        public ValueTask<long> CountEventsAfterAsync(GlobalPosition after, BucketRange buckets, long cap, CancellationToken cancellationToken) => inner.CountEventsAfterAsync(after, buckets, cap, cancellationToken);

        public ValueTask<TimeSpan> GetHorizonAgeAsync(CancellationToken cancellationToken) => ((IStoreMonitor)inner).GetHorizonAgeAsync(cancellationToken);

        public ValueTask<IReadOnlyDictionary<string, int>> CountActiveProcessesAsync(CancellationToken cancellationToken) => inner.CountActiveProcessesAsync(cancellationToken);
    }

    private sealed class UnprobedTransport(InMemoryTransport inner) : ITransport
    {
        public string SystemName => "unprobed";

        public TransportCapabilities Capabilities => inner.Capabilities;

        public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => inner.SendAsync(endpoint, messages, cancellationToken);

        public ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => inner.PublishAsync(topic, messages, cancellationToken);

        public ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken) => inner.CreateReceiverAsync(options, cancellationToken);

        public ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken) => inner.CreateDeadLetterReceiverAsync(endpoint, cancellationToken);

        public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken) => inner.ProvisionAsync(topology, mode, cancellationToken);
    }
}
