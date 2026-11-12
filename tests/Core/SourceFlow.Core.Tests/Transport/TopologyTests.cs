using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Bank.Accounts;
using Bank.Transfers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Testing;
using SourceFlow.Transport;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.Core.Tests.Transport;

/// <summary>UT-TOP (plan 05): the topology model, shared provisioning behaviour, the export and the dead-letter ingestor (WP-3.5).</summary>
[Trait("Category", "Unit")]
public sealed class TopologyTests
{
    // ================================================================ topology from routing

    [Fact]
    [Trait("Id", "UT-TOP-01")]
    [Trait("Covers", "ADR-017;doc 07 3")]
    public async Task Topology_ComesFromRouting()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "transfers";
            sf.AddModule<TransfersModule>();
            sf.AddModule<RemoteAccountCommands>();
            sf.Routing(r =>
            {
#pragma warning disable SF0014 // this service has only the Transfers module; the Accounts service owns these commands
                r.Command<Withdraw>().To("accounts");
                r.Command<Deposit>().To("accounts").ReplyOnReject();
#pragma warning restore SF0014
                r.Subscribe("accounts.events", s => s.Where("type = 'bank.account.withdrawn.v1'"));
                r.Subscribe("audit.events");
            });
            sf.Configure(o =>
            {
                o.DelayedRetries.Clear();
                o.DelayedRetries.Add(TimeSpan.FromSeconds(5));
                o.DelayedRetries.Add(TimeSpan.FromSeconds(30));
            });
        }, cancellationToken: TestContext.Current.CancellationToken);

        TransportTopology topology = host.Runtime.Topology();

        topology.Endpoint.ShouldBe("transfers");
        topology.SendEndpoints.ShouldBe(["accounts"]);
        topology.SubscribedTopics.ShouldBe(["accounts.events", "audit.events"], ignoreOrder: true);
        topology.PublishedTopics.ShouldBeEmpty();
        topology.MaxDeliveries.ShouldBe(1 + 2 + 2, "one delivery, the delayed retries and two spare (doc 07 §4)");
        topology.Filters.Keys.ShouldBe(["accounts.events"]);
        topology.Filters["accounts.events"].ShouldBe("type = 'bank.account.withdrawn.v1'");
        topology.OrderedEndpoint.ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-TOP-02")]
    [Trait("Covers", "doc 07 3")]
    public void Ordering_IsDeclaredPerEndpointAndPublishedTopic()
    {
        var routing = new RoutingBuilder();
        routing.OrderedEndpoint().OrderedTopic("accounts.events");
        routing.Publish<AccountOpened>().AsIs().To("accounts.events").OrderedBy(e => e.AccountId);
        routing.Publish<AccountClosed>().AsIs().To("accounts.audit");

        RoutingTable table = routing.Build("accounts", BankModel.Instance);

        table.OrderedEndpoint.ShouldBeTrue();
        table.OrderedTopics.ShouldBe(["accounts.events"]);

        var unpublished = new RoutingBuilder();
        unpublished.OrderedTopic("payments.events");
        Should.Throw<InvalidOperationException>(() => unpublished.Build("accounts", BankModel.Instance)).Message.ShouldContain("payments.events");
    }

    [Fact]
    [Trait("Id", "UT-TOP-02")]
    public async Task OrderedRouting_ReachesTheTransportTopology()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.Routing(r => r.OrderedEndpoint().OrderedTopic("accounts.events").Publish<AccountOpened>().AsIs().To("accounts.events"));
        }, cancellationToken: TestContext.Current.CancellationToken);

        TransportTopology topology = host.Runtime.Topology();

        topology.OrderedEndpoint.ShouldBeTrue();
        topology.OrderedTopics.ShouldBe(["accounts.events"]);
    }

    // ================================================================ shared provisioning behaviour

    [Fact]
    [Trait("Id", "UT-TOP-03")]
    [Trait("Covers", "ADR-017")]
    public async Task AnInconsistentTopology_FailsBeforeAnyBrokerCall_WithEveryProblem()
    {
        var provisioner = new RecordingProvisioner();
        var topology = new TransportTopology("accounts", ["accounts.events"], ["payments.events"], ["https://sqs/123/x"])
        {
            MaxDeliveries = 0,
            OrderedTopics = ["accounts.audit"],
            Filters = new Dictionary<string, string> { ["orders.events"] = "type = 'x'" },
        };

        foreach (ProvisioningMode mode in Enum.GetValues<ProvisioningMode>())
        {
            ArgumentException error = await Should.ThrowAsync<ArgumentException>(() => provisioner.ProvisionAsync(topology, mode, CancellationToken.None).AsTask());
            error.Message.ShouldContain("MaxDeliveries must be at least 1");
            error.Message.ShouldContain("the ordered topic 'accounts.audit' is not published");
            error.Message.ShouldContain("'orders.events' belongs to a topic the endpoint does not subscribe to");
            error.Message.ShouldContain("looks like a URL or ARN");
        }

        var malformed = new TransportTopology("accounts", [], ["payments.events"], []) { Filters = new Dictionary<string, string> { ["payments.events"] = "type ===" } };
        await Should.ThrowAsync<FormatException>(() => provisioner.ProvisionAsync(malformed, ProvisioningMode.None, CancellationToken.None).AsTask());
        provisioner.Calls.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Id", "UT-TOP-04")]
    [Trait("Covers", "ADR-017;doc 07 5")]
    [InlineData(ProvisioningMode.None, "")]
    [InlineData(ProvisioningMode.Validate, "validate")]
    [InlineData(ProvisioningMode.CreateIfMissing, "create,validate")]
    public async Task EachMode_CallsCreateAndValidateAsSpecified(ProvisioningMode mode, string expected)
    {
        var provisioner = new RecordingProvisioner();

        await provisioner.ProvisionAsync(new TransportTopology("accounts", [], [], []), mode, CancellationToken.None);

        string.Join(',', provisioner.Calls).ShouldBe(expected);
    }

    [Fact]
    [Trait("Id", "UT-TOP-04")]
    public async Task AnUnknownMode_IsRejected()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => new RecordingProvisioner().ProvisionAsync(new TransportTopology("a", [], [], []), (ProvisioningMode)42, CancellationToken.None).AsTask());
    }

    [Theory]
    [Trait("Id", "UT-TOP-05")]
    [Trait("Covers", "ADR-017")]
    [InlineData("Development", false, ProvisioningMode.CreateIfMissing)]
    [InlineData("Production", false, ProvisioningMode.Validate)]
    [InlineData("Staging", false, ProvisioningMode.Validate)]
    [InlineData("Production", true, ProvisioningMode.CreateIfMissing)]
    [InlineData(null, false, ProvisioningMode.CreateIfMissing)]
    public void TheDefaultMode_DependsOnTheEnvironmentAndTransport(string? environment, bool inMemory, ProvisioningMode expected)
    {
        ITransport transport = inMemory ? new InMemoryBroker().CreateTransport() : new RecordingTransport(new InMemoryBroker().CreateTransport());

        ServiceCollectionExtensions.DefaultProvisioning(environment is null ? null : Environment(environment), transport).ShouldBe(expected);
    }

    [Theory]
    [Trait("Id", "UT-TOP-05")]
    [InlineData(null, ProvisioningMode.Validate)]
    [InlineData(ProvisioningMode.None, ProvisioningMode.None)]
    [InlineData(ProvisioningMode.CreateIfMissing, ProvisioningMode.CreateIfMissing)]
    public async Task TheHost_ProvisionsWithTheResolvedMode(ProvisioningMode? configured, ProvisioningMode expected)
    {
        var transport = new RecordingTransport(new InMemoryBroker().CreateTransport());

        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseTransport(_ => transport);
            sf.Provisioning = configured;
        }, services: s => s.AddSingleton<IHostEnvironment>(Environment("Production")), cancellationToken: TestContext.Current.CancellationToken);

        transport.Modes.ShouldBe([expected]);
    }

    [Fact]
    [Trait("Id", "UT-TOP-06")]
    [Trait("Covers", "ADR-017")]
    public async Task Validate_ReportsEveryDifferenceAtOnce()
    {
        InMemoryTransport transport = new InMemoryBroker().CreateTransport();
        var topology = new TransportTopology("accounts", ["accounts.events"], ["payments.events"], []);

        TopologyValidationException error = await Should.ThrowAsync<TopologyValidationException>(() => transport.ProvisionAsync(topology, ProvisioningMode.Validate, CancellationToken.None).AsTask());

        error.Problems.Select(p => p.Kind).ShouldBe([TopologyProblemKind.MissingEndpoint, TopologyProblemKind.MissingTopic, TopologyProblemKind.MissingTopic], ignoreOrder: true);
        string[] lines = error.Message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(1 + error.Problems.Count, "a heading and one line per problem");
        lines.ShouldContain(l => l.StartsWith("MissingEndpoint accounts:", StringComparison.Ordinal));
        lines.ShouldContain(l => l.StartsWith("MissingTopic payments.events:", StringComparison.Ordinal));

        await transport.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, CancellationToken.None);
        await transport.ProvisionAsync(topology, ProvisioningMode.Validate, CancellationToken.None);   // created, then valid
    }

    // ================================================================ export

    [Fact]
    [Trait("Id", "UT-TOP-07")]
    [Trait("Covers", "ADR-017;doc 07 5")]
    public async Task TheDocument_HasTheContractsAndTheTransportsResources()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<Bank.Contracts.ContractsModule>();
            sf.AddModule<AccountsModule>();
            sf.Routing(r =>
            {
                r.Publish<AccountOpened>().As(AccountContracts.ToV1).To("accounts.events");
                r.Publish<AccountClosed>().AsIs().To("accounts.events");
                r.Subscribe("payments.events", s => s.Where("type LIKE 'payments.%'"));
            });
        }, cancellationToken: TestContext.Current.CancellationToken);

        TopologyDocument document = host.Runtime.TopologyDocument();
        JsonObject json = TopologyExport.ToJsonObject(document);

        document.OwnedCommands.ShouldContain("bank.account.open.v1");
        document.PublishedMessages["accounts.events"].ShouldBe(["accounts.account-opened.v1", "bank.account.closed.v1"], ignoreOrder: true);
        json["schemaVersion"]!.GetValue<int>().ShouldBe(1);
        json["service"]!.GetValue<string>().ShouldBe("accounts");
        json["transport"]!.GetValue<string>().ShouldBe(host.Runtime.Transport.SystemName);
        json["endpoint"]!["maxDeliveries"]!.GetValue<int>().ShouldBe(host.Runtime.Topology().MaxDeliveries);
        json["subscribes"]![0]!["filter"]!.GetValue<string>().ShouldBe("type LIKE 'payments.%'");
        JsonArray resources = json["resources"]!.AsArray();
        resources.Select(r => (r!["kind"]!.GetValue<string>(), r["name"]!.GetValue<string>(), r["owned"]!.GetValue<bool>())).ShouldBe(
        [
            ("queue", "accounts", true),
            ("topic", "accounts.events", true),
            ("topic", "payments.events", false),
            ("subscription", "payments.events/accounts", true),
        ]);
    }

    [Fact]
    [Trait("Id", "UT-TOP-08")]
    [Trait("Covers", "doc 07 5")]
    public void TheExport_IsDeterministic()
    {
        var a = new TransportTopology("svc", ["t2", "t1"], ["s2", "s1"], ["e2", "e1"]) { Filters = new Dictionary<string, string> { ["s2"] = "type = 'x.v1'", ["s1"] = "type = 'y.v1'" } };
        var b = new TransportTopology("svc", ["t1", "t2"], ["s1", "s2"], ["e1", "e2"]) { Filters = new Dictionary<string, string> { ["s1"] = "type = 'y.v1'", ["s2"] = "type = 'x.v1'" } };
        var resource = new PhysicalResource(PhysicalResourceKind.Queue, "svc", "svc", true) { Properties = new JsonObject { ["z"] = 1, ["a"] = new JsonObject { ["y"] = 2, ["b"] = 3 } } };

        string first = TopologyExport.ToJson(new TopologyDocument("svc", a) { Physical = new PhysicalTopology("x", [resource]) });
        string second = TopologyExport.ToJson(new TopologyDocument("svc", b) { Physical = new PhysicalTopology("x", [resource with { Properties = new JsonObject { ["a"] = new JsonObject { ["b"] = 3, ["y"] = 2 }, ["z"] = 1 } }]) });

        first.ShouldBe(second);
        first.ShouldEndWith("}\n");
        first.ShouldNotContain("\r");
        first.IndexOf("\"t1\"", StringComparison.Ordinal).ShouldBeLessThan(first.IndexOf("\"t2\"", StringComparison.Ordinal));
        first.IndexOf("\"a\"", StringComparison.Ordinal).ShouldBeLessThan(first.IndexOf("\"z\"", StringComparison.Ordinal));
        first.ShouldContain("\"filter\": \"type = 'y.v1'\"", customMessage: "quotes stay readable");
    }

    [Fact]
    [Trait("Id", "UT-TOP-08")]
    public void TheExport_RejectsDuplicateResourcesAndWritesNoResourcesWithoutADescriber()
    {
        var topology = new TransportTopology("svc", [], [], []);
        var queue = new PhysicalResource(PhysicalResourceKind.Queue, "svc", "svc", true);

        Should.Throw<ArgumentException>(() => TopologyExport.ToJson(new TopologyDocument("svc", topology) { Physical = new PhysicalTopology("x", [queue, queue]) })).Message.ShouldContain("twice");
        JsonObject bare = TopologyExport.ToJsonObject(new TopologyDocument("svc", topology));
        bare.ContainsKey("resources").ShouldBeFalse();
        bare["transport"].ShouldBeNull();
        queue.Role.ShouldBe(PhysicalRoles.Endpoint);
        new PhysicalResource(PhysicalResourceKind.Rule, "r", "t → svc", true).Role.ShouldBe(PhysicalRoles.Rule);
    }

    // ================================================================ dead-letter ingestor

    [Fact]
    [Trait("Id", "UT-TOP-14")]
    [Trait("Covers", "doc 07 3")]
    public async Task TheIngestor_ChecksOnItsInterval_NotOnEveryCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var transport = new RecordingTransport(new InMemoryBroker().CreateTransport());
        await using SourceFlowTestHost host = await AccountsAsync(transport, TimeSpan.FromMinutes(10), ct);

        for (int i = 0; i < 20; i++)
        {
            await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 10), cancellationToken: ct);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500), TimeProvider.System, ct);
        transport.DeadLetterReceives.ShouldBe(1, "one check at startup; commits do not wake the ingestor");
    }

    [Fact]
    [Trait("Id", "UT-TOP-14")]
    public async Task TheIngestor_RecreatesItsReceiverAfterAnError_AndKeepsGoing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var transport = new RecordingTransport(new InMemoryBroker().CreateTransport()) { FailingDeadLetterReceives = 2 };
        await using SourceFlowTestHost host = await AccountsAsync(transport, TimeSpan.FromMilliseconds(100), ct);

        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(15);
        while (transport.DeadLetterReceives < 4 && TimeProvider.System.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, ct);
        }

        transport.DeadLetterReceives.ShouldBeGreaterThanOrEqualTo(4, "two failures, then checks every interval");
        transport.DeadLetterReceivers.ShouldBe(3, "a new receiver after each failure");
        host.Runtime.Transport.ShouldBeSameAs(transport);
    }

    [Fact]
    [Trait("Id", "UT-TOP-14")]
    public async Task TheIngestor_IsOff_WhenDisabled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var transport = new RecordingTransport(new InMemoryBroker().CreateTransport());
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseTransport(_ => transport);
            sf.Configure(o => o.IngestBrokerDeadLetters = false);
        }, cancellationToken: ct);

        await Task.Delay(TimeSpan.FromMilliseconds(300), TimeProvider.System, ct);
        transport.DeadLetterReceivers.ShouldBe(0);
    }

    private static Task<SourceFlowTestHost> AccountsAsync(ITransport transport, TimeSpan interval, CancellationToken ct)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseTransport(_ => transport);
            sf.Configure(o => o.DeadLetterIngestInterval = interval);
        }, cancellationToken: ct);
    }

    private static HostingEnvironment Environment(string name)
    {
        return new HostingEnvironment { EnvironmentName = name, ApplicationName = "tests", ContentRootPath = AppContext.BaseDirectory };
    }

    // The Accounts service's commands, known to a service that only sends them (as a contracts package would register them).
    private sealed class RemoteAccountCommands : ISourceFlowModule
    {
        public static string ModuleName => nameof(RemoteAccountCommands);

        public static void Register(ISourceFlowModuleBuilder builder)
        {
            builder.AddMessage<Withdraw>("bank.account.withdraw", 1);
            builder.AddMessage<Deposit>("bank.account.deposit", 1);
            builder.AddJsonTypeInfoResolver(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
        }
    }

    private sealed class RecordingProvisioner : TopologyProvisioner
    {
        public ConcurrentQueue<string> Calls { get; } = new();

        protected override ValueTask CreateAsync(TransportTopology topology, CancellationToken cancellationToken)
        {
            Calls.Enqueue("create");
            return ValueTask.CompletedTask;
        }

        protected override ValueTask<IReadOnlyList<TopologyProblem>> ValidateAsync(TransportTopology topology, CancellationToken cancellationToken)
        {
            Calls.Enqueue("validate");
            return ValueTask.FromResult<IReadOnlyList<TopologyProblem>>([]);
        }
    }

    /// <summary>Delegates to the in-memory transport; records provisioning modes and dead-letter receives, and can fail receives.</summary>
    private sealed class RecordingTransport(InMemoryTransport inner) : ITransport
    {
        private int _deadLetterReceives;
        private int _deadLetterReceivers;

        public ConcurrentQueue<ProvisioningMode> Modes { get; } = new();

        public int FailingDeadLetterReceives { get; init; }

        public int DeadLetterReceives => Volatile.Read(ref _deadLetterReceives);

        public int DeadLetterReceivers => Volatile.Read(ref _deadLetterReceivers);

        public string SystemName => inner.SystemName;

        public TransportCapabilities Capabilities => inner.Capabilities;

        public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => inner.SendAsync(endpoint, messages, cancellationToken);

        public ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => inner.PublishAsync(topic, messages, cancellationToken);

        public ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken) => inner.CreateReceiverAsync(options, cancellationToken);

        public async ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _deadLetterReceivers);
            return new CountingReceiver(this, await inner.CreateDeadLetterReceiverAsync(endpoint, cancellationToken));
        }

        public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken)
        {
            Modes.Enqueue(mode);
            return inner.ProvisionAsync(topology, ProvisioningMode.CreateIfMissing, cancellationToken);
        }

        private sealed class CountingReceiver(RecordingTransport owner, ITransportReceiver inner) : ITransportReceiver
        {
            public ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
            {
                int count = Interlocked.Increment(ref owner._deadLetterReceives);
                return count <= owner.FailingDeadLetterReceives
                    ? throw new TransportUnavailableException("injected dead-letter receive failure")
                    : inner.ReceiveAsync(maxMessages, wait, cancellationToken);
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
