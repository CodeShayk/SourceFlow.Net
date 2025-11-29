#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using SourceFlow.Stores.EntityFramework.Options;

namespace SourceFlow.Stores.EntityFramework.Services
{
    /// <summary>
    /// Provides resilience policies (retry, circuit breaker, timeout) for database operations.
    /// </summary>
    public class DatabaseResiliencePolicy : IDatabaseResiliencePolicy
    {
        private readonly ResiliencePipeline? _pipeline;
        private readonly bool _isEnabled;

        public DatabaseResiliencePolicy(SourceFlowEfOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            _isEnabled = options.Resilience.Enabled;

            if (_isEnabled)
            {
                _pipeline = BuildResiliencePipeline(options.Resilience);
            }
        }

        /// <summary>
        /// Executes an async operation with resilience policies applied.
        /// </summary>
        public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
        {
            if (!_isEnabled || _pipeline == null)
            {
                return await operation();
            }

            return await _pipeline.ExecuteAsync(async ct => await operation(), CancellationToken.None);
        }

        /// <summary>
        /// Executes an async operation with resilience policies applied.
        /// </summary>
        public async Task ExecuteAsync(Func<Task> operation)
        {
            if (!_isEnabled || _pipeline == null)
            {
                await operation();
                return;
            }

            await _pipeline.ExecuteAsync(async ct => await operation(), CancellationToken.None);
        }

        private ResiliencePipeline BuildResiliencePipeline(ResilienceOptions options)
        {
            var pipelineBuilder = new ResiliencePipelineBuilder();

            // Add timeout policy (innermost - closest to the operation)
            if (options.Timeout.Enabled)
            {
                pipelineBuilder.AddTimeout(new TimeoutStrategyOptions
                {
                    Timeout = TimeSpan.FromMilliseconds(options.Timeout.TimeoutMs)
                });
            }

            // Add retry policy (middle layer)
            if (options.Retry.Enabled)
            {
                var retryOptions = new RetryStrategyOptions
                {
                    MaxRetryAttempts = options.Retry.MaxRetryAttempts,
                    ShouldHandle = new PredicateBuilder().Handle<DbUpdateException>()
                        .Handle<TimeoutException>()
                        .Handle<InvalidOperationException>(ex =>
                            ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)),
                    BackoffType = options.Retry.UseExponentialBackoff
                        ? DelayBackoffType.Exponential
                        : DelayBackoffType.Constant,
                    Delay = TimeSpan.FromMilliseconds(options.Retry.BaseDelayMs),
                    MaxDelay = TimeSpan.FromMilliseconds(options.Retry.MaxDelayMs),
                    UseJitter = options.Retry.UseJitter
                };

                pipelineBuilder.AddRetry(retryOptions);
            }

            // Add circuit breaker policy (outermost - protects the system)
            if (options.CircuitBreaker.Enabled)
            {
                var circuitBreakerOptions = new CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = options.CircuitBreaker.FailureThreshold,
                    BreakDuration = TimeSpan.FromMilliseconds(options.CircuitBreaker.BreakDurationMs),
                    ShouldHandle = new PredicateBuilder().Handle<DbUpdateException>()
                        .Handle<TimeoutException>()
                        .Handle<InvalidOperationException>(ex =>
                            ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                };

                pipelineBuilder.AddCircuitBreaker(circuitBreakerOptions);
            }

            return pipelineBuilder.Build();
        }
    }

    /// <summary>
    /// Interface for database resilience policy.
    /// </summary>
    public interface IDatabaseResiliencePolicy
    {
        /// <summary>
        /// Executes an async operation with resilience policies applied.
        /// </summary>
        Task<T> ExecuteAsync<T>(Func<Task<T>> operation);

        /// <summary>
        /// Executes an async operation with resilience policies applied.
        /// </summary>
        Task ExecuteAsync(Func<Task> operation);
    }
}
