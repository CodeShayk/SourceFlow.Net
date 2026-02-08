namespace SourceFlow.Cloud.Core.Resilience;

/// <summary>
/// Configuration options for circuit breaker behavior
/// </summary>
public class CircuitBreakerOptions
{
    /// <summary>
    /// Number of consecutive failures before opening the circuit
    /// </summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>
    /// Duration to keep circuit open before attempting half-open state
    /// </summary>
    public TimeSpan OpenDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Number of successful calls in half-open state before closing circuit
    /// </summary>
    public int SuccessThreshold { get; set; } = 2;

    /// <summary>
    /// Timeout for individual operations
    /// </summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Exception types that should trigger circuit breaker
    /// </summary>
    public Type[] HandledExceptions { get; set; } = Array.Empty<Type>();

    /// <summary>
    /// Exception types that should NOT trigger circuit breaker
    /// </summary>
    public Type[] IgnoredExceptions { get; set; } = Array.Empty<Type>();

    /// <summary>
    /// Enable fallback to local processing when circuit is open
    /// </summary>
    public bool EnableFallback { get; set; } = true;
}
