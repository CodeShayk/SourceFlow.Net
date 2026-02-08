namespace SourceFlow.Cloud.Core.Resilience;

/// <summary>
/// Exception thrown when circuit breaker is open and requests are blocked
/// </summary>
public class CircuitBreakerOpenException : Exception
{
    public CircuitState State { get; }
    public TimeSpan RetryAfter { get; }

    public CircuitBreakerOpenException(CircuitState state, TimeSpan retryAfter)
        : base($"Circuit breaker is {state}. Retry after {retryAfter.TotalSeconds:F1} seconds.")
    {
        State = state;
        RetryAfter = retryAfter;
    }

    public CircuitBreakerOpenException(string message) : base(message)
    {
        State = CircuitState.Open;
    }

    public CircuitBreakerOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
        State = CircuitState.Open;
    }
}
