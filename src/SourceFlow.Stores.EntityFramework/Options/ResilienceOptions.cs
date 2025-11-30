namespace SourceFlow.Stores.EntityFramework.Options
{
    /// <summary>
    /// Configuration options for resilience patterns (retry, circuit breaker, etc.)
    /// </summary>
    public class ResilienceOptions
    {
        /// <summary>
        /// Gets or sets whether resilience policies are enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the retry policy configuration.
        /// </summary>
        public RetryPolicyOptions Retry { get; set; } = new RetryPolicyOptions();

        /// <summary>
        /// Gets or sets the circuit breaker policy configuration.
        /// </summary>
        public CircuitBreakerOptions CircuitBreaker { get; set; } = new CircuitBreakerOptions();

        /// <summary>
        /// Gets or sets the timeout policy configuration.
        /// </summary>
        public TimeoutOptions Timeout { get; set; } = new TimeoutOptions();
    }

    /// <summary>
    /// Configuration for retry policies.
    /// </summary>
    public class RetryPolicyOptions
    {
        /// <summary>
        /// Gets or sets whether retry is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of retry attempts.
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 3;

        /// <summary>
        /// Gets or sets the base delay between retries in milliseconds.
        /// </summary>
        public int BaseDelayMs { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the maximum delay between retries in milliseconds.
        /// </summary>
        public int MaxDelayMs { get; set; } = 30000;

        /// <summary>
        /// Gets or sets whether to use exponential backoff.
        /// </summary>
        public bool UseExponentialBackoff { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to add jitter to retry delays.
        /// </summary>
        public bool UseJitter { get; set; } = true;
    }

    /// <summary>
    /// Configuration for circuit breaker policies.
    /// </summary>
    public class CircuitBreakerOptions
    {
        /// <summary>
        /// Gets or sets whether circuit breaker is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the number of consecutive failures before breaking the circuit.
        /// </summary>
        public int FailureThreshold { get; set; } = 5;

        /// <summary>
        /// Gets or sets the duration in milliseconds the circuit stays open before attempting to close.
        /// </summary>
        public int BreakDurationMs { get; set; } = 30000;

        /// <summary>
        /// Gets or sets the number of successful calls required to close the circuit when in half-open state.
        /// </summary>
        public int SuccessThreshold { get; set; } = 2;
    }

    /// <summary>
    /// Configuration for timeout policies.
    /// </summary>
    public class TimeoutOptions
    {
        /// <summary>
        /// Gets or sets whether timeout is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the timeout duration in milliseconds.
        /// </summary>
        public int TimeoutMs { get; set; } = 30000;
    }
}
