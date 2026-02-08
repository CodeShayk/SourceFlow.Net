using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SourceFlow.Cloud.Core.Resilience;

/// <summary>
/// Implementation of circuit breaker pattern for fault tolerance
/// </summary>
public class CircuitBreaker : ICircuitBreaker
{
    private readonly CircuitBreakerOptions _options;
    private readonly ILogger<CircuitBreaker> _logger;
    private readonly object _lock = new();

    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures = 0;
    private int _consecutiveSuccesses = 0;
    private DateTime? _openedAt;
    private Exception? _lastException;

    // Statistics
    private int _totalCalls = 0;
    private int _successfulCalls = 0;
    private int _failedCalls = 0;
    private int _rejectedCalls = 0;
    private DateTime? _lastStateChange;
    private DateTime? _lastFailure;

    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public event EventHandler<CircuitBreakerStateChangedEventArgs>? StateChanged;

    public CircuitBreaker(IOptions<CircuitBreakerOptions> options, ILogger<CircuitBreaker> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        CheckAndUpdateState();

        lock (_lock)
        {
            _totalCalls++;

            if (_state == CircuitState.Open)
            {
                _rejectedCalls++;
                var retryAfter = _openedAt.HasValue
                    ? _options.OpenDuration - (DateTime.UtcNow - _openedAt.Value)
                    : _options.OpenDuration;

                _logger.LogWarning("Circuit breaker is open. Rejecting call. Retry after {RetryAfter}s",
                    retryAfter.TotalSeconds);

                throw new CircuitBreakerOpenException(_state, retryAfter);
            }
        }

        try
        {
            // Execute with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.OperationTimeout);

            var result = await operation();

            OnSuccess();
            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            OnFailure(ex);
            throw;
        }
    }

    public async Task ExecuteAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async () =>
        {
            await operation();
            return true;
        }, cancellationToken);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _logger.LogInformation("Manually resetting circuit breaker to Closed state");
            TransitionTo(CircuitState.Closed);
            _consecutiveFailures = 0;
            _consecutiveSuccesses = 0;
            _openedAt = null;
            _lastException = null;
        }
    }

    public void Trip()
    {
        lock (_lock)
        {
            _logger.LogWarning("Manually tripping circuit breaker to Open state");
            TransitionTo(CircuitState.Open);
            _openedAt = DateTime.UtcNow;
        }
    }

    public CircuitBreakerStatistics GetStatistics()
    {
        lock (_lock)
        {
            return new CircuitBreakerStatistics
            {
                CurrentState = _state,
                TotalCalls = _totalCalls,
                SuccessfulCalls = _successfulCalls,
                FailedCalls = _failedCalls,
                RejectedCalls = _rejectedCalls,
                LastStateChange = _lastStateChange,
                LastFailure = _lastFailure,
                LastException = _lastException,
                ConsecutiveFailures = _consecutiveFailures,
                ConsecutiveSuccesses = _consecutiveSuccesses
            };
        }
    }

    private void CheckAndUpdateState()
    {
        lock (_lock)
        {
            if (_state == CircuitState.Open && _openedAt.HasValue)
            {
                var elapsed = DateTime.UtcNow - _openedAt.Value;
                if (elapsed >= _options.OpenDuration)
                {
                    _logger.LogInformation("Circuit breaker transitioning from Open to HalfOpen after {Duration}s",
                        elapsed.TotalSeconds);
                    TransitionTo(CircuitState.HalfOpen);
                }
            }
        }
    }

    private void OnSuccess()
    {
        lock (_lock)
        {
            _successfulCalls++;
            _consecutiveFailures = 0;
            _consecutiveSuccesses++;

            if (_state == CircuitState.HalfOpen)
            {
                if (_consecutiveSuccesses >= _options.SuccessThreshold)
                {
                    _logger.LogInformation(
                        "Circuit breaker transitioning from HalfOpen to Closed after {Count} successful calls",
                        _consecutiveSuccesses);
                    TransitionTo(CircuitState.Closed);
                    _consecutiveSuccesses = 0;
                }
            }
        }
    }

    private void OnFailure(Exception ex)
    {
        lock (_lock)
        {
            // Check if this exception should be ignored
            if (ShouldIgnoreException(ex))
            {
                _logger.LogDebug("Ignoring exception {ExceptionType} for circuit breaker",
                    ex.GetType().Name);
                return;
            }

            _failedCalls++;
            _consecutiveSuccesses = 0;
            _consecutiveFailures++;
            _lastException = ex;
            _lastFailure = DateTime.UtcNow;

            _logger.LogWarning(ex,
                "Circuit breaker recorded failure ({ConsecutiveFailures}/{Threshold}): {Message}",
                _consecutiveFailures, _options.FailureThreshold, ex.Message);

            if (_state == CircuitState.HalfOpen)
            {
                // Immediately open on failure in half-open state
                _logger.LogWarning("Circuit breaker transitioning from HalfOpen to Open after failure");
                TransitionTo(CircuitState.Open);
                _openedAt = DateTime.UtcNow;
                _consecutiveFailures = 0;
            }
            else if (_state == CircuitState.Closed && _consecutiveFailures >= _options.FailureThreshold)
            {
                _logger.LogError(ex,
                    "Circuit breaker transitioning from Closed to Open after {Count} consecutive failures",
                    _consecutiveFailures);
                TransitionTo(CircuitState.Open);
                _openedAt = DateTime.UtcNow;
            }
        }
    }

    private bool ShouldIgnoreException(Exception ex)
    {
        var exceptionType = ex.GetType();

        // Check if exception is in ignored list
        if (_options.IgnoredExceptions.Any(t => t.IsAssignableFrom(exceptionType)))
        {
            return true;
        }

        // If handled exceptions are specified, only count those
        if (_options.HandledExceptions.Length > 0)
        {
            return !_options.HandledExceptions.Any(t => t.IsAssignableFrom(exceptionType));
        }

        return false;
    }

    private void TransitionTo(CircuitState newState)
    {
        var previousState = _state;
        _state = newState;
        _lastStateChange = DateTime.UtcNow;

        StateChanged?.Invoke(this, new CircuitBreakerStateChangedEventArgs(
            previousState, newState, _lastException));
    }
}
