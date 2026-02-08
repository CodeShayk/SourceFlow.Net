using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SourceFlow.Cloud.Core.Resilience;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Integration tests for AWS circuit breaker pattern implementation
/// Tests automatic circuit opening on SQS/SNS service failures, half-open state recovery,
/// circuit closing on successful recovery, and circuit breaker configuration and monitoring
/// </summary>
[Collection("AWS Integration Tests")]
public class AwsCircuitBreakerTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IAwsTestEnvironment _environment = null!;
    private readonly ILogger<AwsCircuitBreakerTests> _logger;
    private readonly string _testPrefix;
    
    public AwsCircuitBreakerTests(ITestOutputHelper output)
    {
        _output = output;
        _testPrefix = $"cb-test-{Guid.NewGuid():N}";
        
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        
        _logger = loggerFactory.CreateLogger<AwsCircuitBreakerTests>();
    }
    
    public async Task InitializeAsync()
    {
        _environment = await AwsTestEnvironmentFactory.CreateLocalStackEnvironmentAsync(_testPrefix);
    }
    
    public async Task DisposeAsync()
    {
        await _environment.DisposeAsync();
    }
    
    /// <summary>
    /// Test that circuit breaker opens automatically after consecutive SQS failures
    /// Validates: Requirement 7.1 - Automatic circuit opening on SQS service failures
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_OpensAutomatically_OnConsecutiveSqsFailures()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 3,
            OpenDuration = TimeSpan.FromSeconds(5),
            SuccessThreshold = 2,
            OperationTimeout = TimeSpan.FromSeconds(2)
        };
        
        var circuitBreaker = CreateCircuitBreaker(options);
        var invalidQueueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/nonexistent-queue";
        
        // Track state changes
        var stateChanges = new List<CircuitState>();
        circuitBreaker.StateChanged += (sender, args) => stateChanges.Add(args.NewState);
        
        // Act - Execute operations that will fail
        for (int i = 0; i < options.FailureThreshold; i++)
        {
            try
            {
                await circuitBreaker.ExecuteAsync(async () =>
                {
                    await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = invalidQueueUrl,
                        MessageBody = "test"
                    });
                });
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Expected failure {i + 1}: {ex.Message}");
            }
        }
        
        // Assert - Circuit should be open
        Assert.Equal(CircuitState.Open, circuitBreaker.State);
        Assert.Contains(CircuitState.Open, stateChanges);
        
        var stats = circuitBreaker.GetStatistics();
        Assert.Equal(options.FailureThreshold, stats.FailedCalls);
        Assert.Equal(options.FailureThreshold, stats.ConsecutiveFailures);
        
        // Verify that subsequent calls are rejected
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
        {
            await circuitBreaker.ExecuteAsync(async () =>
            {
                await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = invalidQueueUrl,
                    MessageBody = "test"
                });
            });
        });
        
        var finalStats = circuitBreaker.GetStatistics();
        Assert.True(finalStats.RejectedCalls > 0, "Circuit breaker should reject calls when open");
    }
    
    /// <summary>
    /// Test that circuit breaker opens automatically after consecutive SNS failures
    /// Validates: Requirement 7.1 - Automatic circuit opening on SNS service failures
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_OpensAutomatically_OnConsecutiveSnsFailures()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 3,
            OpenDuration = TimeSpan.FromSeconds(5),
            SuccessThreshold = 2,
            OperationTimeout = TimeSpan.FromSeconds(2)
        };
        
        var circuitBreaker = CreateCircuitBreaker(options);
        var invalidTopicArn = "arn:aws:sns:us-east-1:000000000000:nonexistent-topic";
        
        // Track state changes
        var stateChanges = new List<CircuitState>();
        circuitBreaker.StateChanged += (sender, args) => stateChanges.Add(args.NewState);
        
        // Act - Execute operations that will fail
        for (int i = 0; i < options.FailureThreshold; i++)
        {
            try
            {
                await circuitBreaker.ExecuteAsync(async () =>
                {
                    await _environment.SnsClient.PublishAsync(new PublishRequest
                    {
                        TopicArn = invalidTopicArn,
                        Message = "test"
                    });
                });
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Expected failure {i + 1}: {ex.Message}");
            }
        }
        
        // Assert - Circuit should be open
        Assert.Equal(CircuitState.Open, circuitBreaker.State);
        Assert.Contains(CircuitState.Open, stateChanges);
        
        var stats = circuitBreaker.GetStatistics();
        Assert.Equal(options.FailureThreshold, stats.FailedCalls);
    }
    
    /// <summary>
    /// Test that circuit breaker transitions to half-open state after timeout
    /// Validates: Requirement 7.1 - Half-open state and recovery testing
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_TransitionsToHalfOpen_AfterTimeout()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(2), // Short duration for testing
            SuccessThreshold = 2,
            OperationTimeout = TimeSpan.FromSeconds(2)
        };
        
        var circuitBreaker = CreateCircuitBreaker(options);
        var invalidQueueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/nonexistent-queue";
        
        // Track state changes
        var stateChanges = new List<(CircuitState Previous, CircuitState New)>();
        circuitBreaker.StateChanged += (sender, args) => 
            stateChanges.Add((args.PreviousState, args.NewState));
        
        // Act - Trigger circuit to open
        for (int i = 0; i < options.FailureThreshold; i++)
        {
            try
            {
                await circuitBreaker.ExecuteAsync(async () =>
                {
                    await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = invalidQueueUrl,
                        MessageBody = "test"
                    });
                });
            }
            catch { /* Expected */ }
        }
        
        Assert.Equal(CircuitState.Open, circuitBreaker.State);
        
        // Wait for circuit to transition to half-open
        await Task.Delay(options.OpenDuration + TimeSpan.FromMilliseconds(500));
        
        // Trigger state check by attempting an operation
        var queueUrl = await _environment.CreateStandardQueueAsync($"{_testPrefix}-halfopen");
        try
        {
            await circuitBreaker.ExecuteAsync(async () =>
            {
                await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = queueUrl,
                    MessageBody = "test"
                });
            });
        }
        catch { /* May fail, but should trigger state transition */ }
        
        // Assert - Circuit should have transitioned through half-open
        Assert.Contains(stateChanges, sc => sc.Previous == CircuitState.Open && sc.New == CircuitState.HalfOpen);
        
        // Cleanup
        await _environment.DeleteQueueAsync(queueUrl);
    }
    
    /// <summary>
    /// Test that circuit breaker closes after successful operations in half-open state
    /// Validates: Requirement 7.1 - Circuit closing on successful recovery
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_ClosesSuccessfully_AfterRecoveryInHalfOpenState()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(2),
            SuccessThreshold = 2, // Need 2 successes to close
            OperationTimeout = TimeSpan.FromSeconds(5)
        };
        
        var circuitBreaker = CreateCircuitBreaker(options);
        var invalidQueueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/nonexistent-queue";
        var validQueueUrl = await _environment.CreateStandardQueueAsync($"{_testPrefix}-recovery");
        
        // Track state changes
        var stateChanges = new List<(CircuitState Previous, CircuitState New, DateTime Time)>();
        circuitBreaker.StateChanged += (sender, args) => 
            stateChanges.Add((args.PreviousState, args.NewState, args.ChangedAt));
        
        try
        {
            // Act - Step 1: Open the circuit
            for (int i = 0; i < options.FailureThreshold; i++)
            {
                try
                {
                    await circuitBreaker.ExecuteAsync(async () =>
                    {
                        await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                        {
                            QueueUrl = invalidQueueUrl,
                            MessageBody = "test"
                        });
                    });
                }
                catch { /* Expected */ }
            }
            
            Assert.Equal(CircuitState.Open, circuitBreaker.State);
            _output.WriteLine($"Circuit opened at {DateTime.UtcNow}");
            
            // Step 2: Wait for half-open transition
            await Task.Delay(options.OpenDuration + TimeSpan.FromMilliseconds(500));
            
            // Step 3: Execute successful operations to close the circuit
            for (int i = 0; i < options.SuccessThreshold; i++)
            {
                await circuitBreaker.ExecuteAsync(async () =>
                {
                    await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = validQueueUrl,
                        MessageBody = $"Recovery test {i}"
                    });
                });
                _output.WriteLine($"Successful operation {i + 1} completed");
            }
            
            // Assert - Circuit should be closed
            Assert.Equal(CircuitState.Closed, circuitBreaker.State);
            
            // Verify state transition sequence: Closed -> Open -> HalfOpen -> Closed
            Assert.Contains(stateChanges, sc => sc.Previous == CircuitState.Closed && sc.New == CircuitState.Open);
            Assert.Contains(stateChanges, sc => sc.Previous == CircuitState.Open && sc.New == CircuitState.HalfOpen);
            Assert.Contains(stateChanges, sc => sc.Previous == CircuitState.HalfOpen && sc.New == CircuitState.Closed);
            
            var stats = circuitBreaker.GetStatistics();
            Assert.True(stats.SuccessfulCalls >= options.SuccessThreshold, 
                $"Should have at least {options.SuccessThreshold} successful calls, got {stats.SuccessfulCalls}");
            Assert.Equal(CircuitState.Closed, stats.CurrentState);
            
            _output.WriteLine($"Circuit closed successfully. Stats: {stats.SuccessfulCalls} successes, {stats.FailedCalls} failures");
        }
        finally
        {
            // Cleanup
            await _environment.DeleteQueueAsync(validQueueUrl);
        }
    }
    
    /// <summary>
    /// Test that circuit breaker reopens if failure occurs in half-open state
    /// Validates: Requirement 7.1 - Half-open state failure handling
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_ReopensImmediately_OnFailureInHalfOpenState()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(2),
            SuccessThreshold = 2,
            OperationTimeout = TimeSpan.FromSeconds(2)
        };
        
        var circuitBreaker = CreateCircuitBreaker(options);
        var invalidQueueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/nonexistent-queue";
        
        // Track state changes
        var stateChanges = new List<(CircuitState Previous, CircuitState New)>();
        circuitBreaker.StateChanged += (sender, args) => 
            stateChanges.Add((args.PreviousState, args.NewState));
        
        // Act - Step 1: Open the circuit
        for (int i = 0; i < options.FailureThreshold; i++)
        {
            try
            {
                await circuitBreaker.ExecuteAsync(async () =>
                {
                    await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = invalidQueueUrl,
                        MessageBody = "test"
                    });
                });
            }
            catch { /* Expected */ }
        }
        
        Assert.Equal(CircuitState.Open, circuitBreaker.State);
        
        // Step 2: Wait for half-open transition
        await Task.Delay(options.OpenDuration + TimeSpan.FromMilliseconds(500));
        
        // Step 3: Fail in half-open state
        try
        {
            await circuitBreaker.ExecuteAsync(async () =>
            {
                await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = invalidQueueUrl,
                    MessageBody = "test"
                });
            });
        }
        catch { /* Expected */ }
        
        // Assert - Circuit should be open again
        Assert.Equal(CircuitState.Open, circuitBreaker.State);
        
        // Verify we transitioned: Open -> HalfOpen -> Open
        var halfOpenToOpen = stateChanges.Where(sc => 
            sc.Previous == CircuitState.HalfOpen && sc.New == CircuitState.Open).ToList();
        Assert.NotEmpty(halfOpenToOpen);
    }
    
    /// <summary>
    /// Test circuit breaker configuration options
    /// Validates: Requirement 7.1 - Circuit breaker configuration
    /// </summary>
    [Fact]
    public void CircuitBreaker_Configuration_IsAppliedCorrectly()
    {
        // Arrange & Act
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 10,
            OpenDuration = TimeSpan.FromMinutes(5),
            SuccessThreshold = 3,
            OperationTimeout = TimeSpan.FromSeconds(60),
            EnableFallback = true
        };
        
        var circuitBreaker = CreateCircuitBreaker(options);
        
        // Assert - Initial state
        Assert.Equal(CircuitState.Closed, circuitBreaker.State);
        
        var stats = circuitBreaker.GetStatistics();
        Assert.Equal(CircuitState.Closed, stats.CurrentState);
        Assert.Equal(0, stats.TotalCalls);
        Assert.Equal(0, stats.FailedCalls);
        Assert.Equal(0, stats.SuccessfulCalls);
        Assert.Equal(0, stats.RejectedCalls);
    }
    
    /// <summary>
    /// Test circuit breaker statistics and monitoring
    /// Validates: Requirement 7.1 - Circuit breaker monitoring
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_Statistics_TrackOperationsCorrectly()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 5,
            OpenDuration = TimeSpan.FromSeconds(10),
            SuccessThreshold = 2,
            OperationTimeout = TimeSpan.FromSeconds(5)
        };
        
        var circuitBreaker = CreateCircuitBreaker(options);
        var validQueueUrl = await _environment.CreateStandardQueueAsync($"{_testPrefix}-stats");
        var invalidQueueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/nonexistent-queue";
        
        try
        {
            // Act - Execute mix of successful and failed operations
            // Successful operations
            for (int i = 0; i < 3; i++)
            {
                await circuitBreaker.ExecuteAsync(async () =>
                {
                    await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = validQueueUrl,
                        MessageBody = $"Success {i}"
                    });
                });
            }
            
            // Failed operations (but not enough to open circuit)
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    await circuitBreaker.ExecuteAsync(async () =>
                    {
                        await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                        {
                            QueueUrl = invalidQueueUrl,
                            MessageBody = "test"
                        });
                    });
                }
                catch { /* Expected */ }
            }
            
            // Assert - Verify statistics
            var stats = circuitBreaker.GetStatistics();
            
            Assert.Equal(5, stats.TotalCalls);
            Assert.Equal(3, stats.SuccessfulCalls);
            Assert.Equal(2, stats.FailedCalls);
            Assert.Equal(0, stats.RejectedCalls);
            Assert.Equal(CircuitState.Closed, stats.CurrentState);
            Assert.Equal(2, stats.ConsecutiveFailures);
            Assert.NotNull(stats.LastFailure);
            Assert.NotNull(stats.LastException);
            
            _output.WriteLine($"Statistics: Total={stats.TotalCalls}, Success={stats.SuccessfulCalls}, " +
                            $"Failed={stats.FailedCalls}, Rejected={stats.RejectedCalls}");
        }
        finally
        {
            // Cleanup
            await _environment.DeleteQueueAsync(validQueueUrl);
        }
    }
    
    /// <summary>
    /// Test circuit breaker with manual reset
    /// Validates: Requirement 7.1 - Manual circuit breaker control
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_ManualReset_ClosesCircuitImmediately()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromMinutes(10), // Long duration
            SuccessThreshold = 2,
            OperationTimeout = TimeSpan.FromSeconds(2)
        };
        
        var circuitBreaker = CreateCircuitBreaker(options);
        var invalidQueueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/nonexistent-queue";
        
        // Act - Open the circuit
        for (int i = 0; i < options.FailureThreshold; i++)
        {
            try
            {
                await circuitBreaker.ExecuteAsync(async () =>
                {
                    await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = invalidQueueUrl,
                        MessageBody = "test"
                    });
                });
            }
            catch { /* Expected */ }
        }
        
        Assert.Equal(CircuitState.Open, circuitBreaker.State);
        
        // Manually reset the circuit
        circuitBreaker.Reset();
        
        // Assert - Circuit should be closed immediately
        Assert.Equal(CircuitState.Closed, circuitBreaker.State);
        
        var stats = circuitBreaker.GetStatistics();
        Assert.Equal(0, stats.ConsecutiveFailures);
        Assert.Equal(0, stats.ConsecutiveSuccesses);
    }
    
    /// <summary>
    /// Test circuit breaker with manual trip
    /// Validates: Requirement 7.1 - Manual circuit breaker control
    /// </summary>
    [Fact]
    public void CircuitBreaker_ManualTrip_OpensCircuitImmediately()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 10,
            OpenDuration = TimeSpan.FromSeconds(5),
            SuccessThreshold = 2,
            OperationTimeout = TimeSpan.FromSeconds(5)
        };
        
        var circuitBreaker = CreateCircuitBreaker(options);
        
        Assert.Equal(CircuitState.Closed, circuitBreaker.State);
        
        // Act - Manually trip the circuit
        circuitBreaker.Trip();
        
        // Assert - Circuit should be open immediately
        Assert.Equal(CircuitState.Open, circuitBreaker.State);
    }
    
    /// <summary>
    /// Test circuit breaker state change events
    /// Validates: Requirement 7.1 - Circuit breaker monitoring
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_StateChangeEvents_AreRaisedCorrectly()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(2),
            SuccessThreshold = 1,
            OperationTimeout = TimeSpan.FromSeconds(2)
        };
        
        var circuitBreaker = CreateCircuitBreaker(options);
        var invalidQueueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/nonexistent-queue";
        var validQueueUrl = await _environment.CreateStandardQueueAsync($"{_testPrefix}-events");
        
        // Track state change events
        var events = new List<CircuitBreakerStateChangedEventArgs>();
        circuitBreaker.StateChanged += (sender, args) => events.Add(args);
        
        try
        {
            // Act - Trigger state changes
            // 1. Closed -> Open
            for (int i = 0; i < options.FailureThreshold; i++)
            {
                try
                {
                    await circuitBreaker.ExecuteAsync(async () =>
                    {
                        await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                        {
                            QueueUrl = invalidQueueUrl,
                            MessageBody = "test"
                        });
                    });
                }
                catch { /* Expected */ }
            }
            
            // 2. Wait for Open -> HalfOpen
            await Task.Delay(options.OpenDuration + TimeSpan.FromMilliseconds(500));
            
            // 3. HalfOpen -> Closed
            await circuitBreaker.ExecuteAsync(async () =>
            {
                await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = validQueueUrl,
                    MessageBody = "recovery"
                });
            });
            
            // Assert - Verify events were raised
            Assert.NotEmpty(events);
            
            // Should have: Closed->Open, Open->HalfOpen, HalfOpen->Closed
            var closedToOpen = events.FirstOrDefault(e => 
                e.PreviousState == CircuitState.Closed && e.NewState == CircuitState.Open);
            Assert.NotNull(closedToOpen);
            Assert.NotNull(closedToOpen.LastException);
            
            var openToHalfOpen = events.FirstOrDefault(e => 
                e.PreviousState == CircuitState.Open && e.NewState == CircuitState.HalfOpen);
            Assert.NotNull(openToHalfOpen);
            
            var halfOpenToClosed = events.FirstOrDefault(e => 
                e.PreviousState == CircuitState.HalfOpen && e.NewState == CircuitState.Closed);
            Assert.NotNull(halfOpenToClosed);
            
            _output.WriteLine($"Total state change events: {events.Count}");
            foreach (var evt in events)
            {
                _output.WriteLine($"  {evt.PreviousState} -> {evt.NewState} at {evt.ChangedAt}");
            }
        }
        finally
        {
            // Cleanup
            await _environment.DeleteQueueAsync(validQueueUrl);
        }
    }
    
    /// <summary>
    /// Test circuit breaker with operation timeout
    /// Validates: Requirement 7.1 - Operation timeout handling
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_OperationTimeout_TriggersFailure()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(5),
            SuccessThreshold = 2,
            OperationTimeout = TimeSpan.FromMilliseconds(100) // Very short timeout
        };
        
        var circuitBreaker = CreateCircuitBreaker(options);
        
        // Act - Execute operations that will timeout
        for (int i = 0; i < options.FailureThreshold; i++)
        {
            try
            {
                await circuitBreaker.ExecuteAsync(async () =>
                {
                    // Simulate slow operation
                    await Task.Delay(TimeSpan.FromSeconds(5));
                });
            }
            catch (OperationCanceledException)
            {
                _output.WriteLine($"Operation {i + 1} timed out as expected");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Operation {i + 1} failed: {ex.GetType().Name}");
            }
        }
        
        // Assert - Circuit should be open due to timeouts
        Assert.Equal(CircuitState.Open, circuitBreaker.State);
        
        var stats = circuitBreaker.GetStatistics();
        Assert.True(stats.FailedCalls >= options.FailureThreshold);
    }
    
    /// <summary>
    /// Test circuit breaker with concurrent operations
    /// Validates: Requirement 7.1 - Thread-safe circuit breaker operation
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_ConcurrentOperations_AreThreadSafe()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 10,
            OpenDuration = TimeSpan.FromSeconds(5),
            SuccessThreshold = 2,
            OperationTimeout = TimeSpan.FromSeconds(5)
        };
        
        var circuitBreaker = CreateCircuitBreaker(options);
        var validQueueUrl = await _environment.CreateStandardQueueAsync($"{_testPrefix}-concurrent");
        
        try
        {
            // Act - Execute concurrent operations
            var tasks = Enumerable.Range(0, 20).Select(async i =>
            {
                try
                {
                    await circuitBreaker.ExecuteAsync(async () =>
                    {
                        await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
                        {
                            QueueUrl = validQueueUrl,
                            MessageBody = $"Concurrent message {i}"
                        });
                    });
                    return true;
                }
                catch
                {
                    return false;
                }
            });
            
            var results = await Task.WhenAll(tasks);
            
            // Assert - All operations should complete without race conditions
            var stats = circuitBreaker.GetStatistics();
            Assert.Equal(20, stats.TotalCalls);
            Assert.True(stats.SuccessfulCalls > 0);
            Assert.Equal(CircuitState.Closed, stats.CurrentState);
            
            _output.WriteLine($"Concurrent operations: {stats.SuccessfulCalls} succeeded, {stats.FailedCalls} failed");
        }
        finally
        {
            // Cleanup
            await _environment.DeleteQueueAsync(validQueueUrl);
        }
    }
    
    private ICircuitBreaker CreateCircuitBreaker(CircuitBreakerOptions options)
    {
        var optionsWrapper = Options.Create(options);
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        var logger = loggerFactory.CreateLogger<CircuitBreaker>();
        
        return new CircuitBreaker(optionsWrapper, logger);
    }
}
