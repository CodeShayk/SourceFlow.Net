using System;

namespace SourceFlow.Cloud.Resilience;

/// <summary>
/// Event arguments for circuit breaker state changes
/// </summary>
public class CircuitBreakerStateChangedEventArgs : EventArgs
{
    public CircuitState PreviousState { get; }
    public CircuitState NewState { get; }
    public DateTime ChangedAt { get; }
    public Exception? LastException { get; }

    public CircuitBreakerStateChangedEventArgs(
        CircuitState previousState,
        CircuitState newState,
        Exception? lastException = null)
    {
        PreviousState = previousState;
        NewState = newState;
        ChangedAt = DateTime.UtcNow;
        LastException = lastException;
    }
}
