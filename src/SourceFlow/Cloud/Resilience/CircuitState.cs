namespace SourceFlow.Cloud.Resilience;

/// <summary>
/// Represents the state of a circuit breaker
/// </summary>
public enum CircuitState
{
    /// <summary>
    /// Circuit is closed, requests flow normally
    /// </summary>
    Closed,

    /// <summary>
    /// Circuit is open, requests are blocked
    /// </summary>
    Open,

    /// <summary>
    /// Circuit is half-open, testing if service has recovered
    /// </summary>
    HalfOpen
}
