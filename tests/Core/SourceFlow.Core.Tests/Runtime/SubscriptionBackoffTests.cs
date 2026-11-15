using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Hosting;
using SourceFlow.Pipeline;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-SUB-29..30 (WP-C.7, CR-29): handler subscriptions back off before parking, and park once across a crash.</summary>
[Trait("Category", "Unit")]
public sealed class SubscriptionBackoffTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-SUB-29")]
    [Trait("Covers", "doc 06 2")]
    public async Task ATransientHandlerError_BacksOff_AndDoesNotPark()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "drips";
            sf.AddModule<CoreTestModule>();
            sf.AddModule<FlakyHandlerModule>();
            sf.Configure(o =>
            {
                o.ImmediateRetries = 0;
                o.DelayedRetries.Clear();
                o.DelayedRetries.Add(TimeSpan.FromSeconds(10));
                o.DelayedRetries.Add(TimeSpan.FromSeconds(20));
            });
        }, time, cancellationToken: Ct);
        SourceFlowHost service = host.Services.GetRequiredService<SourceFlowHost>();
        var id = DripId.New();
        FlakyDripHandler.TransientFailuresLeft[id] = 4;   // two attempts (each: the batch, then the event alone)

        await host.SendAsync(new Drip(id, "flaky"), cancellationToken: Ct);
        await Until(() => Calls(id) == 2);
        (await HandlerDeadLettersAsync(host)).ShouldBeEmpty("backing off, not parked");

        time.Advance(TimeSpan.FromSeconds(5));
        service.Pulse();
        await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);
        Calls(id).ShouldBe(2, "not before the first delay");

        time.Advance(TimeSpan.FromSeconds(6));
        service.Pulse();
        await Until(() => Calls(id) == 4);
        time.Advance(TimeSpan.FromSeconds(21));
        service.Pulse();
        await Until(() => Calls(id) == 5);
        await host.WaitForIdleAsync();

        (await HandlerDeadLettersAsync(host)).ShouldBeEmpty("it recovered within the delayed-retry budget");
    }

    [Fact]
    [Trait("Id", "UT-SUB-30")]
    [Trait("Covers", "doc 06 2")]
    public async Task ACrashBetweenParkingAndTheCheckpoint_LeavesOneDeadLetter()
    {
        var crash = new CrashOnce(FaultPoints.SubscriptionAfterParkBeforeCheckpoint);
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "drips";
            sf.AddModule<CoreTestModule>();
            sf.AddModule<FlakyHandlerModule>();
            sf.Configure(o => o.ImmediateRetries = 0);
        }, services: s => s.AddSingleton<IFaultInjector>(crash), cancellationToken: Ct);
        var id = DripId.New();
        FlakyDripHandler.Poisoned[id] = true;

        await host.SendAsync(new Drip(id, "poison for the handler"), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        crash.Crashed.ShouldBeTrue();
        Calls(id).ShouldBe(4, "handled again after the crash (batch, then alone, each time), since the checkpoint had not moved");
        DeadLetter parked = (await HandlerDeadLettersAsync(host)).ShouldHaveSingleItem("parked again as the same dead letter");
        parked.Reason.ShouldBe("SubscriptionFailed");
    }

    private static int Calls(DripId id)
    {
        return FlakyDripHandler.Calls.GetValueOrDefault(id);
    }

    private static async Task<IReadOnlyList<DeadLetter>> HandlerDeadLettersAsync(SourceFlowTestHost host)
    {
        return [.. (await host.Store.QueryAsync(new DeadLetterQuery(), Ct)).Where(d => d.Consumer.StartsWith("handler:", StringComparison.Ordinal))];
    }

    private static async Task Until(Func<bool> condition)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow().AddSeconds(10);
        while (!condition())
        {
            TimeProvider.System.GetUtcNow().ShouldBeLessThan(deadline, "the condition did not become true");
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }
    }

    // An event handler that fails only for the drips a test lists, transiently or permanently. Nested, so the generator
    // leaves it out of CoreTestModule; FlakyHandlerModule adds it to these tests' hosts only.
    public sealed class FlakyDripHandler : IEventHandler<Dripped>
    {
        internal static ConcurrentDictionary<DripId, int> TransientFailuresLeft { get; } = new();

        internal static ConcurrentDictionary<DripId, bool> Poisoned { get; } = new();

        internal static ConcurrentDictionary<DripId, int> Calls { get; } = new();

        public ValueTask HandleAsync(Dripped e, EventContext ctx, CancellationToken cancellationToken)
        {
            Calls.AddOrUpdate(e.DripId, 1, (_, n) => n + 1);
            if (Poisoned.ContainsKey(e.DripId))
            {
                throw new PermanentException("This drip can never be handled.");
            }

            if (TransientFailuresLeft.TryGetValue(e.DripId, out int left) && left > 0)
            {
                TransientFailuresLeft[e.DripId] = left - 1;
                throw new TimeoutException("The dependency timed out.");
            }

            return default;
        }
    }

    private sealed class FlakyHandlerModule : ISourceFlowModule
    {
        public static string ModuleName => nameof(FlakyHandlerModule);

        public static void Register(ISourceFlowModuleBuilder builder)
        {
            builder.AddEventHandler<FlakyDripHandler, Dripped>();
        }
    }

    private sealed class CrashOnce(string point) : IFaultInjector
    {
        private int _crashed;

        public bool Crashed => Volatile.Read(ref _crashed) == 1;

        public ValueTask InjectAsync(string at, CancellationToken cancellationToken)
        {
            return at == point && Interlocked.Exchange(ref _crashed, 1) == 0
                ? throw new InvalidOperationException("Injected crash at " + at + ".")
                : ValueTask.CompletedTask;
        }
    }
}
