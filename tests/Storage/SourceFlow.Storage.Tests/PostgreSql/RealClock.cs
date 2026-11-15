namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>
/// Waits on the system clock. Only for integration tests against an external clock that cannot be faked (database
/// lease expiry, LISTEN latency, eventual consistency of a real host); everything else uses FakeTimeProvider (plan 04 §8).
/// </summary>
internal static class RealClock
{
    public static Task WaitAsync(TimeSpan delay)
    {
        return Task.Delay(delay, TimeProvider.System, TestContext.Current.CancellationToken);
    }
}
