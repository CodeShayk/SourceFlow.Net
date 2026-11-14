using System.Diagnostics;
using System.Diagnostics.Metrics;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Diagnostics;
using SourceFlow.Runtime;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>Runs alone: an ActivitySource listener anywhere in the process changes what is allocated.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryIsolation
{
    public const string Name = "telemetry isolation";
}

/// <summary>UT-HC-11 and UT-HC-16 (WP-C.16, ADR-019, doc 08, CR-47): the messaging conventions and no-listener cost.</summary>
[Collection(TelemetryIsolation.Name)]
[Trait("Category", "Unit")]
public sealed class TelemetryConventionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-HC-11")]
    [Trait("Covers", "doc 08 3.1;ADR-019")]
    public async Task TheStandardInstruments_AndTheProcessSpan_CarryTheRequiredAttributes()
    {
        string[] standard =
        [
            SourceFlowTelemetry.Instruments.OperationDuration, SourceFlowTelemetry.Instruments.SentMessages,
            SourceFlowTelemetry.Instruments.ConsumedMessages, SourceFlowTelemetry.Instruments.ProcessDuration,
        ];
        var recorded = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
        using var meters = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SourceFlowTelemetry.MeterName && standard.Contains(instrument.Name))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            lock (recorded)
            {
                (recorded.TryGetValue(instrument.Name, out List<Dictionary<string, object?>>? list) ? list : recorded[instrument.Name] = []).Add(copy);
            }
        }

        meters.SetMeasurementEventCallback<long>((i, _, t, _) => Record(i, t));
        meters.SetMeasurementEventCallback<double>((i, _, t, _) => Record(i, t));
        meters.Start();
        var spans = new List<Activity>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceFlowTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (spans)
                {
                    spans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(activities);

        // The service publishes AccountOpened to a topic it also subscribes to: sent, then consumed and processed.
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.Routing(r =>
            {
                r.Publish<AccountOpened>().AsIs().To("accounts.events");
                r.Subscribe("accounts.events");
            });
        }, cancellationToken: Ct);
        (await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 1m), cancellationToken: Ct)).Status.ShouldBe(CommandStatus.Accepted);
        await host.WaitForIdleAsync();

        foreach (string name in standard)
        {
            List<Dictionary<string, object?>> measurements = recorded.GetValueOrDefault(name).ShouldNotBeNull($"{name} was recorded");
            measurements.ShouldAllBe(m => m.ContainsKey(SourceFlowTelemetry.Tags.MessagingSystem) && m[SourceFlowTelemetry.Tags.MessagingSystem] != null, $"{name} carries messaging.system");
            measurements.ShouldAllBe(m => m.ContainsKey(SourceFlowTelemetry.Tags.Destination) && m[SourceFlowTelemetry.Tags.Destination] != null, $"{name} carries messaging.destination.name");
        }

        recorded[SourceFlowTelemetry.Instruments.OperationDuration].ShouldAllBe(m => (string?)m[SourceFlowTelemetry.Tags.OperationName] == "publish");
        recorded[SourceFlowTelemetry.Instruments.ProcessDuration].ShouldAllBe(m => m.ContainsKey(SourceFlowTelemetry.Tags.ErrorType), "error.type is present (null on success)");

        Activity process;
        lock (spans)
        {
            process = spans.First(s => s.OperationName == "process accounts");
        }

        process.GetTagItem(SourceFlowTelemetry.Tags.MessagingSystem).ShouldBe(host.Runtime.Transport.SystemName);
        process.GetTagItem(SourceFlowTelemetry.Tags.Destination).ShouldBe("accounts");
        lock (spans)
        {
            spans.ShouldContain(s => s.OperationName == "handle bank.account.open", "span names use the logical message name");
        }
    }

    [Fact]
    [Trait("Id", "UT-HC-16")]
    [Trait("Covers", "doc 08 2.1")]
    public void WithoutAListener_StartingASpan_AllocatesNothing()
    {
        SourceFlowTelemetry.ActivitySource.HasListeners().ShouldBeFalse("no listener is attached while this collection runs");
        string target = "accounts.events";
        SourceFlowTelemetry.Start("send", target, ActivityKind.Producer);   // warm up

        int started = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            if (SourceFlowTelemetry.Start("send", target, ActivityKind.Producer) is not null)
            {
                started++;
            }
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        started.ShouldBe(0);
        allocated.ShouldBe(0, "no span name is built without a listener");
    }
}
