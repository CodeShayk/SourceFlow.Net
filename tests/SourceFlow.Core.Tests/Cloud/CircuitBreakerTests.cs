using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SourceFlow.Cloud.Resilience;

namespace SourceFlow.Core.Tests.Cloud
{
    [TestFixture]
    [Category("Unit")]
    public class CircuitBreakerTests
    {
        private CircuitBreaker CreateBreaker(Action<CircuitBreakerOptions>? configure = null)
        {
            var opts = new CircuitBreakerOptions
            {
                FailureThreshold = 3,
                OpenDuration = TimeSpan.FromMinutes(1),
                SuccessThreshold = 2,
                OperationTimeout = TimeSpan.FromSeconds(30)
            };
            configure?.Invoke(opts);
            return new CircuitBreaker(Options.Create(opts), NullLogger<CircuitBreaker>.Instance);
        }

        // ─── Initial state ────────────────────────────────────────────────────────

        [Test]
        public void InitialState_IsClosed()
        {
            var cb = CreateBreaker();
            Assert.That(cb.State, Is.EqualTo(CircuitState.Closed));
        }

        // ─── Closed → Open after FailureThreshold consecutive failures ───────────

        [Test]
        public async Task ClosedToOpen_AfterExactlyFailureThresholdConsecutiveFailures()
        {
            var cb = CreateBreaker(o => o.FailureThreshold = 3);

            for (var i = 0; i < 2; i++)
            {
                try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException("fail")); }
                catch (InvalidOperationException) { }
            }

            Assert.That(cb.State, Is.EqualTo(CircuitState.Closed),
                "Should still be Closed after FailureThreshold-1 failures");

            try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException("fail")); }
            catch (InvalidOperationException) { }

            Assert.That(cb.State, Is.EqualTo(CircuitState.Open),
                "Should be Open after reaching FailureThreshold failures");
        }

        // ─── Open → throws CircuitBreakerOpenException without calling operation ──

        [Test]
        public async Task WhenOpen_ExecuteAsync_ThrowsCircuitBreakerOpenExceptionWithoutCallingOperation()
        {
            var cb = CreateBreaker(o => o.FailureThreshold = 1);

            try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException()); }
            catch (InvalidOperationException) { }

            Assert.That(cb.State, Is.EqualTo(CircuitState.Open));

            var operationCalled = false;
            Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
                await cb.ExecuteAsync<int>(() =>
                {
                    operationCalled = true;
                    return Task.FromResult(42);
                }));

            Assert.That(operationCalled, Is.False, "Operation lambda must not be called when circuit is Open");
        }

        // ─── Open → HalfOpen after OpenDuration elapses ───────────────────────────

        [Test]
        public async Task OpenToHalfOpen_AfterOpenDurationElapses()
        {
            var cb = CreateBreaker(o =>
            {
                o.FailureThreshold = 1;
                o.OpenDuration = TimeSpan.FromMilliseconds(50);
            });

            try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException()); }
            catch (InvalidOperationException) { }

            Assert.That(cb.State, Is.EqualTo(CircuitState.Open));

            await Task.Delay(100);

            // Trigger state re-evaluation by calling ExecuteAsync (will succeed, transitioning to HalfOpen first)
            var result = await cb.ExecuteAsync(() => Task.FromResult(1));
            Assert.That(cb.State, Is.EqualTo(CircuitState.Closed).Or.EqualTo(CircuitState.HalfOpen),
                "After OpenDuration elapses, circuit should transition out of Open");
        }

        // ─── HalfOpen → Closed after SuccessThreshold successes ──────────────────

        [Test]
        public async Task HalfOpenToClosed_AfterSuccessThresholdSuccesses()
        {
            var cb = CreateBreaker(o =>
            {
                o.FailureThreshold = 1;
                o.OpenDuration = TimeSpan.FromMilliseconds(50);
                o.SuccessThreshold = 2;
            });

            // Trip the breaker
            try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException()); }
            catch (InvalidOperationException) { }

            // Wait for Open → HalfOpen
            await Task.Delay(100);

            // SuccessThreshold successes
            await cb.ExecuteAsync(() => Task.FromResult(1));
            await cb.ExecuteAsync(() => Task.FromResult(1));

            Assert.That(cb.State, Is.EqualTo(CircuitState.Closed),
                "Should be Closed after SuccessThreshold successes in HalfOpen");
        }

        // ─── HalfOpen → Open on first failure ─────────────────────────────────────

        [Test]
        public async Task HalfOpenToOpen_OnFirstFailure()
        {
            var cb = CreateBreaker(o =>
            {
                o.FailureThreshold = 1;
                o.OpenDuration = TimeSpan.FromMilliseconds(50);
                o.SuccessThreshold = 3;
            });

            try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException()); }
            catch (InvalidOperationException) { }

            await Task.Delay(100);

            // One success to confirm we've entered HalfOpen, then fail
            await cb.ExecuteAsync(() => Task.FromResult(1));

            // Only if SuccessThreshold > 1 we are still in HalfOpen; we need to verify
            // that a failure now opens the circuit
            Assert.That(cb.State, Is.EqualTo(CircuitState.HalfOpen),
                "Should be in HalfOpen after one success when SuccessThreshold is 3");

            try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException()); }
            catch (InvalidOperationException) { }

            Assert.That(cb.State, Is.EqualTo(CircuitState.Open),
                "Should transition back to Open on failure in HalfOpen");
        }

        // ─── HandledExceptions only trip the breaker ─────────────────────────────

        [Test]
        public async Task HandledExceptions_OnlyListedTypeTripsBreaker()
        {
            var cb = CreateBreaker(o =>
            {
                o.FailureThreshold = 1;
                o.HandledExceptions = new[] { typeof(InvalidOperationException) };
            });

            // ArgumentException is NOT in HandledExceptions: should propagate but not trip
            try { await cb.ExecuteAsync<int>(() => throw new ArgumentException("not handled")); }
            catch (ArgumentException) { }

            Assert.That(cb.State, Is.EqualTo(CircuitState.Closed),
                "Unlisted exception type should NOT trip the breaker");

            // InvalidOperationException IS in HandledExceptions: should trip
            try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException("handled")); }
            catch (InvalidOperationException) { }

            Assert.That(cb.State, Is.EqualTo(CircuitState.Open),
                "Listed exception type should trip the breaker");
        }

        // ─── IgnoredExceptions do not record a failure ────────────────────────────

        [Test]
        public async Task IgnoredExceptions_DoNotRecordFailure()
        {
            var cb = CreateBreaker(o =>
            {
                o.FailureThreshold = 1;
                o.IgnoredExceptions = new[] { typeof(ArgumentException) };
            });

            // Throw the ignored exception multiple times — circuit must stay Closed
            for (var i = 0; i < 5; i++)
            {
                try { await cb.ExecuteAsync<int>(() => throw new ArgumentException("ignored")); }
                catch (ArgumentException) { }
            }

            Assert.That(cb.State, Is.EqualTo(CircuitState.Closed),
                "Ignored exceptions must not trip the breaker");

            var stats = cb.GetStatistics();
            Assert.That(stats.FailedCalls, Is.EqualTo(0),
                "FailedCalls should not increment for ignored exceptions");
        }

        // ─── Reset() forces Closed ────────────────────────────────────────────────

        [Test]
        public async Task Reset_ForcesClosed_RegardlessOfCurrentState()
        {
            var cb = CreateBreaker(o => o.FailureThreshold = 1);

            try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException()); }
            catch (InvalidOperationException) { }

            Assert.That(cb.State, Is.EqualTo(CircuitState.Open));

            cb.Reset();

            Assert.That(cb.State, Is.EqualTo(CircuitState.Closed));
        }

        // ─── Trip() forces Open from Closed ───────────────────────────────────────

        [Test]
        public void Trip_ForcesOpen_FromClosed()
        {
            var cb = CreateBreaker();
            Assert.That(cb.State, Is.EqualTo(CircuitState.Closed));

            cb.Trip();

            Assert.That(cb.State, Is.EqualTo(CircuitState.Open));
        }

        // ─── GetStatistics() returns correct counts ───────────────────────────────

        [Test]
        public async Task GetStatistics_ReturnsCorrectCountsAfterSequenceOfOperations()
        {
            var cb = CreateBreaker(o => o.FailureThreshold = 5);

            // 2 successes
            await cb.ExecuteAsync(() => Task.FromResult(1));
            await cb.ExecuteAsync(() => Task.FromResult(1));

            // 2 failures (threshold is 5 so still closed)
            try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException()); }
            catch (InvalidOperationException) { }
            try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException()); }
            catch (InvalidOperationException) { }

            var stats = cb.GetStatistics();

            Assert.That(stats.TotalCalls, Is.EqualTo(4));
            Assert.That(stats.SuccessfulCalls, Is.EqualTo(2));
            Assert.That(stats.FailedCalls, Is.EqualTo(2));
            Assert.That(stats.RejectedCalls, Is.EqualTo(0));
        }

        [Test]
        public async Task GetStatistics_RejectedCalls_IncrementWhenCircuitOpen()
        {
            var cb = CreateBreaker(o => o.FailureThreshold = 1);

            try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException()); }
            catch (InvalidOperationException) { }

            // Two rejected calls
            try { await cb.ExecuteAsync(() => Task.FromResult(1)); }
            catch (CircuitBreakerOpenException) { }
            try { await cb.ExecuteAsync(() => Task.FromResult(1)); }
            catch (CircuitBreakerOpenException) { }

            var stats = cb.GetStatistics();
            Assert.That(stats.RejectedCalls, Is.EqualTo(2));
            Assert.That(stats.TotalCalls, Is.EqualTo(3)); // 1 failure + 2 rejected
        }

        // ─── StateChanged event raised on every state transition ─────────────────

        [Test]
        public async Task StateChanged_RaisedOnEveryTransitionWithCorrectFromAndToState()
        {
            var cb = CreateBreaker(o =>
            {
                o.FailureThreshold = 1;
                o.OpenDuration = TimeSpan.FromMilliseconds(50);
                o.SuccessThreshold = 1;
            });

            var events = new List<(CircuitState From, CircuitState To)>();
            cb.StateChanged += (_, args) => events.Add((args.PreviousState, args.NewState));

            // Closed → Open
            try { await cb.ExecuteAsync<int>(() => throw new InvalidOperationException()); }
            catch (InvalidOperationException) { }

            // Wait for Open → HalfOpen transition
            await Task.Delay(100);

            // HalfOpen → Closed
            await cb.ExecuteAsync(() => Task.FromResult(1));

            Assert.That(events.Count, Is.GreaterThanOrEqualTo(2),
                "At least two state change events should have been raised");

            Assert.That(events[0], Is.EqualTo((CircuitState.Closed, CircuitState.Open)),
                "First transition should be Closed → Open");

            // Find the HalfOpen → Closed transition
            Assert.That(events, Has.Some.EqualTo((CircuitState.HalfOpen, CircuitState.Closed)),
                "Should have a HalfOpen → Closed transition");
        }

        // ─── Thread safety ────────────────────────────────────────────────────────

        [Test]
        public async Task ThreadSafety_ConcurrentCallsProduceConsistentStatistics()
        {
            const int total = 50;
            var cb = CreateBreaker(o =>
            {
                o.FailureThreshold = 100; // keep it open long enough
                o.OperationTimeout = TimeSpan.FromSeconds(5);
            });

            var tasks = new Task[total];
            for (var i = 0; i < total; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        await cb.ExecuteAsync(() => Task.FromResult(1));
                    }
                    catch (CircuitBreakerOpenException) { }
                });
            }

            await Task.WhenAll(tasks);

            var stats = cb.GetStatistics();
            Assert.That(stats.TotalCalls + stats.RejectedCalls, Is.GreaterThanOrEqualTo(total),
                "TotalCalls + RejectedCalls must account for all attempted calls (no corrupt state)");
            Assert.That(stats.SuccessfulCalls + stats.FailedCalls, Is.LessThanOrEqualTo(stats.TotalCalls));
        }

        // ─── OperationTimeout records a failure ───────────────────────────────────

        [Test]
        public async Task OperationTimeout_RecordsFailure_WhenOperationExceedsTimeout()
        {
            var cb = CreateBreaker(o =>
            {
                o.FailureThreshold = 10;
                o.OperationTimeout = TimeSpan.FromMilliseconds(50);
            });

            // Operation that takes longer than the timeout
            try
            {
                await cb.ExecuteAsync(async () =>
                {
                    await Task.Delay(500);
                    return 1;
                });
            }
            catch (OperationCanceledException) { }

            var stats = cb.GetStatistics();
            Assert.That(stats.FailedCalls, Is.GreaterThan(0),
                "A timed-out operation should record a failure");
        }
    }
}
