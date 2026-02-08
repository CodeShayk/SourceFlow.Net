namespace SourceFlow.Cloud.Core.Resilience;

/// <summary>
/// Circuit breaker pattern for fault tolerance
/// </summary>
public interface ICircuitBreaker
{
    /// <summary>
    /// Current state of the circuit breaker
    /// </summary>
    CircuitState State { get; }

    /// <summary>
    /// Execute an operation with circuit breaker protection
    /// </summary>
    Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute an operation with circuit breaker protection (void return)
    /// </summary>
    Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Manually reset the circuit breaker to closed state
    /// </summary>
    void Reset();

    /// <summary>
    /// Manually trip the circuit breaker to open state
    /// </summary>
    void Trip();

    /// <summary>
    /// Event raised when circuit breaker state changes
    /// </summary>
    event EventHandler<CircuitBreakerStateChangedEventArgs> StateChanged;

    /// <summary>
    /// Get statistics about circuit breaker behavior
    /// </summary>
    CircuitBreakerStatistics GetStatistics();
}

/// <summary>
/// Statistics about circuit breaker behavior
/// </summary>
public class CircuitBreakerStatistics
{
    public CircuitState CurrentState { get; set; }
    public int TotalCalls { get; set; }
    public int SuccessfulCalls { get; set; }
    public int FailedCalls { get; set; }
    public int RejectedCalls { get; set; }
    public DateTime? LastStateChange { get; set; }
    public DateTime? LastFailure { get; set; }
    public Exception? LastException { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int ConsecutiveSuccesses { get; set; }
}
