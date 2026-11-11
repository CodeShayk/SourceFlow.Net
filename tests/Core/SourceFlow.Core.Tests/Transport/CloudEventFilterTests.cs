using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Runtime;
using SourceFlow.Testing;
using SourceFlow.Testing.Conformance;
using SourceFlow.Transport;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.Core.Tests.Transport;

/// <summary>UT-CE-01..08 (doc 07 §7, WP-3.1): the CloudEvents SQL subscription filter.</summary>
[Trait("Category", "Unit")]
public sealed class CloudEventFilterTests
{
    private static readonly Dictionary<string, string> Event = new()
    {
        ["id"] = "e-1",
        ["source"] = "urn:sourceflow:accounts",
        ["type"] = "bank.account.opened.v1",
        ["subject"] = "acct-42",
        ["sftenant"] = "eu",
        ["sfstreamversion"] = "7",
        ["flag"] = "true",
    };

    [Theory]
    [Trait("Id", "UT-CE-01")]
    [InlineData("type = 'bank.account.opened.v1'", true)]
    [InlineData("type = 'bank.account.closed.v1' OR sftenant = 'eu'", true)]
    [InlineData("type = 'x' OR sftenant = 'eu' AND subject = 'nope'", false)]        // AND binds tighter than OR
    [InlineData("(type = 'x' OR sftenant = 'eu') AND subject = 'acct-42'", true)]
    [InlineData("NOT sftenant = 'us'", true)]
    [InlineData("sftenant = 'eu' XOR subject = 'acct-42'", false)]
    [InlineData("sfstreamversion > 5 AND sfstreamversion <= 7", true)]
    [InlineData("sfstreamversion + 3 = 10 AND sfstreamversion * 2 % 5 = 4", true)]
    [InlineData("sfstreamversion <> 7", false)]
    [InlineData("-sfstreamversion < 0", true)]
    [InlineData("flag = TRUE", true)]
    public void Operators_AndPrecedence(string filter, bool expected)
    {
        CloudEventFilter.Parse(filter).Matches(Event).ShouldBe(expected);
    }

    [Theory]
    [Trait("Id", "UT-CE-02")]
    [InlineData("type LIKE 'bank.%'", true)]
    [InlineData("type LIKE 'bank.account.opened.v_'", true)]
    [InlineData("type LIKE 'BANK.%'", false)]                                      // case sensitive
    [InlineData("type NOT LIKE 'bank.%'", false)]
    [InlineData("subject LIKE 'acct-4\\_'", false)]                                  // escaped wildcard is literal
    [InlineData("source LIKE '%:accounts'", true)]
    public void Like(string filter, bool expected)
    {
        CloudEventFilter.Parse(filter).Matches(Event).ShouldBe(expected);
    }

    [Theory]
    [Trait("Id", "UT-CE-03")]
    [InlineData("type IN ('a.v1', 'bank.account.opened.v1')", true)]
    [InlineData("sftenant NOT IN ('eu', 'us')", false)]
    [InlineData("sfstreamversion IN (1, 7)", true)]
    [InlineData("EXISTS subject", true)]
    [InlineData("EXISTS sfactor", false)]
    [InlineData("NOT EXISTS sfactor AND EXISTS sftenant", true)]
    public void InAndExists(string filter, bool expected)
    {
        CloudEventFilter.Parse(filter).Matches(Event).ShouldBe(expected);
    }

    [Theory]
    [Trait("Id", "UT-CE-04")]
    [InlineData("sfactor = 'x'")]                 // missing attribute: an error, never a match
    [InlineData("NOT (sfactor = 'x')")]           // ... even negated (the whole expression errs)
    [InlineData("subject > 3")]                   // 'acct-42' is not an integer
    [InlineData("sfstreamversion / 0 = 1")]
    [InlineData("subject = TRUE")]
    public void EvaluationErrors_NeverMatch(string filter)
    {
        CloudEventFilter.Parse(filter).Matches(Event).ShouldBeFalse();
    }

    [Theory]
    [Trait("Id", "UT-CE-05")]
    [InlineData("LOWER('ABC') = 'abc' AND UPPER(sftenant) = 'EU'", true)]
    [InlineData("LENGTH(subject) = 7", true)]
    [InlineData("CONCAT(sftenant, '-', subject) = 'eu-acct-42'", true)]
    [InlineData("TRIM('  x ') = 'x' AND ABS(-3) = 3", true)]
    [InlineData("INT(sfstreamversion) = 7 AND STRING(7) = sfstreamversion AND BOOL(flag)", true)]
    public void Functions(string filter, bool expected)
    {
        CloudEventFilter.Parse(filter).Matches(Event).ShouldBe(expected);
    }

    [Theory]
    [Trait("Id", "UT-CE-06")]
    [InlineData("type =", "a value")]
    [InlineData("type = 'open", "a closing quote")]
    [InlineData("type = 'a' AND", "a value")]
    [InlineData("Type = 'a'", "lower-case")]
    [InlineData("FOO(type) = 'a'", "a known function")]
    [InlineData("type NOT = 'a'", "LIKE or IN after NOT")]
    [InlineData("type = 'a' 'b'", "the end of the filter")]
    [InlineData("type LIKE subject", "a string pattern")]
    [InlineData("LENGTH(type, subject) = 1", "one argument")]
    public void ParseErrors_NameWhatWasExpected(string filter, string expected)
    {
        FormatException error = Should.Throw<FormatException>(() => CloudEventFilter.Parse(filter));

        error.Message.ShouldContain(expected);
        error.Message.ShouldContain("position");
        CloudEventFilter.TryParse(filter, out _, out string? message).ShouldBeFalse();
        message.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Id", "UT-CE-07")]
    public void NativeTranslation_ExactTypesAndPrefixes()
    {
        CloudEventFilter.Parse("type = 'a.v1' OR type IN ('b.v1', 'c.v2')").ExactTypes.ShouldBe(["a.v1", "b.v1", "c.v2"]);
        CloudEventFilter.Parse("type = 'a.v1' AND sftenant = 'eu'").ExactTypes.ShouldBeNull();
        CloudEventFilter.Parse("type LIKE 'payments.%' OR type LIKE 'refunds.%'").TypePrefixes.ShouldBe(["payments.", "refunds."]);
        CloudEventFilter.Parse("type LIKE 'pay_ments.%'").TypePrefixes.ShouldBeNull();

        CloudEventFilter quoted = CloudEventFilter.ForTypes(["it's.v1", "b.v1"]);
        quoted.Expression.ShouldBe("type IN ('b.v1', 'it\\'s.v1')");
        quoted.ExactTypes.ShouldBe(["b.v1", "it's.v1"]);
        quoted.Matches(new Dictionary<string, string> { ["type"] = "it's.v1" }).ShouldBeTrue();
        Should.Throw<ArgumentException>(() => CloudEventFilter.ForTypes([]));
    }

    [Fact]
    [Trait("Id", "UT-CE-08")]
    public void Envelopes_AreMatchedOnTheirCloudEventsAttributes()
    {
        var envelope = new MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = "bank.account.opened",
            TypeVersion = 2,
            Source = "accounts",
            Time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Subject = "acct-1",
            TenantId = "eu",
            Body = Encoding.UTF8.GetBytes("{}"),
        };

        CloudEventFilter.Parse("type = 'bank.account.opened.v2' AND sftenant = 'eu' AND subject = 'acct-1'").Matches(envelope).ShouldBeTrue();
        CloudEventFilter.Parse("type = 'bank.account.opened'").Matches(envelope).ShouldBeFalse();   // the CloudEvents type carries the version
        CloudEventFilter.Parse("EXISTS sfactor").Matches(envelope).ShouldBeFalse();
    }
}

/// <summary>UT-RT-21..22, UT-RX-22..24 (WP-3.1): subscription filters from routing, client-side filtering, transport errors, lock renewal.</summary>
[Trait("Category", "Unit")]
public sealed class TransportCoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-RT-21")]
    public void SubscriptionFilter_CombinesOnlyTypesWithWhere()
    {
        RuntimeModel model = new SourceFlowModelBuilder().AddModule<TckModule>().Build();
        var routing = new RoutingBuilder();
        routing.Subscribe("only", s => s.Only<TckAdded>());
        routing.Subscribe("both", s => s.Only<TckAdded>().Where("sftenant = 'eu'"));
        routing.Subscribe("where", s => s.Where("type LIKE 'tck.%'"));
        routing.Subscribe("all");

        RoutingTable table = routing.Build("tck", model);

        table.Subscriptions.Single(s => s.Topic == "only").Filter.ShouldBe("type = 'tck.tally.added.v1'");
        table.Subscriptions.Single(s => s.Topic == "both").Filter.ShouldBe("(type = 'tck.tally.added.v1') AND (sftenant = 'eu')");
        table.Subscriptions.Single(s => s.Topic == "where").Filter.ShouldBe("type LIKE 'tck.%'");
        table.Subscriptions.Single(s => s.Topic == "all").Filter.ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "UT-RT-22")]
    public void SubscriptionFilter_Malformed_FailsAtConfiguration()
    {
        Should.Throw<FormatException>(() => new RoutingBuilder().Subscribe("t", s => s.Where("type = ")));
    }

    [Fact]
    [Trait("Id", "UT-RX-22")]
    [Trait("Covers", "doc 07 7")]
    public async Task WithoutNativeFilters_TheCoreFiltersEventsOnReceipt()
    {
        var broker = new InMemoryBroker();
        var transport = new NoFilterTransport(broker.CreateTransport());
        var observations = new TckObservations();
        await using SourceFlowTestHost reader = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "reader";
            sf.AddModule<TckModule>();
            sf.UseTransport(_ => transport);
            sf.Routing(r => r.Subscribe("tallies", s => s.Where("sftenant = 'eu'")));
        }, broker: broker, services: s => s.AddSingleton(observations));

        MessageEnvelope eu = reader.Runtime.CreateEnvelope(new TckAdded(TckTallyId.New(), 1, 1)) with { TenantId = "eu" };
        MessageEnvelope us = reader.Runtime.CreateEnvelope(new TckAdded(TckTallyId.New(), 1, 1)) with { TenantId = "us" };
        await transport.PublishAsync("tallies", [us, eu], Ct);
        await reader.WaitForIdleAsync();

        observations.Handled.Where(h => h.Service == "reader").Select(h => h.EventId).ShouldBe([eu.MessageId]);
        broker.DeliveredCount("reader").ShouldBe(2);   // both arrived; the filtered one was acknowledged unhandled
    }

    [Fact]
    [Trait("Id", "UT-RX-23")]
    public void TransportErrors_AreClassifiedByTheirTransience()
    {
        var classifier = new ErrorClassifier();

        classifier.Classify(new TransportUnavailableException("down")).ShouldBe(HandlingOutcome.Transient);
        classifier.Classify(new TransportThrottledException("slow down") { RetryAfter = TimeSpan.FromSeconds(1) }).ShouldBe(HandlingOutcome.Transient);
        classifier.Classify(new DestinationNotFoundException("nowhere")).ShouldBe(HandlingOutcome.Permanent);
        var tooLarge = new SendFailure(new MessageId(Guid.CreateVersion7()), new MessageTooLargeException("t", 10, 5));
        classifier.Classify(new TransportSendException("q", [tooLarge], 0)).ShouldBe(HandlingOutcome.Permanent);
        var outage = new SendFailure(new MessageId(Guid.CreateVersion7()), new TransportUnavailableException("down"));
        classifier.Classify(new TransportSendException("q", [outage], 3)).ShouldBe(HandlingOutcome.Transient);
    }

    [Fact]
    [Trait("Id", "UT-RX-24")]
    [Trait("Covers", "doc 07 4")]
    public async Task ALongHandler_KeepsItsLock_SoTheMessageIsNotRedelivered()
    {
        var broker = new InMemoryBroker();
        var slow = new SlowMiddleware(TimeSpan.FromMilliseconds(1600));
        await using SourceFlowTestHost owner = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "owner";
            sf.AddModule<TckModule>();
            sf.Configure(o => o.ReceiveLockDuration = TimeSpan.FromSeconds(1));
            sf.UseMiddleware(_ => slow, PipelineStage.Receive);
        }, broker: broker, services: s => s.AddSingleton(new TckObservations()));

        broker.Send("owner", owner.Runtime.CreateEnvelope(new TckAdd(TckTallyId.New(), 1)));
        await owner.WaitForIdleAsync();

        slow.Calls.ShouldBe(1);   // without renewal the 1 s lock would expire during the 1.6 s handler
    }

    private sealed class SlowMiddleware(TimeSpan delay) : IMessageMiddleware
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(delay, TimeProvider.System, context.CancellationToken);
            await next(context);
        }
    }

    // A broker without native subscription filters: every subscribed event is delivered.
    private sealed class NoFilterTransport(ITransport inner) : ITransport
    {
        public string SystemName => inner.SystemName;

        public TransportCapabilities Capabilities { get; } = inner.Capabilities with { SubscriptionFilters = false };

        public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            return inner.SendAsync(endpoint, messages, cancellationToken);
        }

        public ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            return inner.PublishAsync(topic, messages, cancellationToken);
        }

        public ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken)
        {
            return inner.CreateReceiverAsync(options with { Filters = new Dictionary<string, string>() }, cancellationToken);
        }

        public ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken)
        {
            return inner.CreateDeadLetterReceiverAsync(endpoint, cancellationToken);
        }

        public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken)
        {
            return inner.ProvisionAsync(topology with { Filters = new Dictionary<string, string>() }, mode, cancellationToken);
        }
    }
}
