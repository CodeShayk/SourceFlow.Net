using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SourceFlow.Cloud.Resilience;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Tests for Azure circuit breaker pattern behavior including automatic circuit opening,
/// half-open testing, and recovery for Azure services.
/// Validates Requirements 6.1.
/// </summary>
[Trait("Category", "Unit")]
public class AzureCircuitBreakerTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ILoggerFactory _loggerFactory;
    private ICircuitBreaker? _circuitBreaker;
    private int _callCount;
    private bool _shouldFail;

    public AzureCircuitBreakerTests(ITestOutputHelper output)
    {
        _output = output;
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddDebug();
            builder.AddXUnit(output);
            builder.SetMinimumLevel(LogLevel.Information);
        });
    }

    public Task InitializeAsync()
    {
        _callCount = 0;
        _shouldFail = false;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    #region Circuit Opening Tests (Requirement 6.1)

    /// <summary>
    /// Test: Circuit breaker opens after threshold failures
    /// Validates: Requirements 6.1
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_OpensAfterThresholdFailures()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 3,
            OpenDuration = TimeSpan.FromSeconds(10),
            SuccessThreshold = 2
        };

        _circuitBreaker = new CircuitBreaker(
            Options.Create(options),
            _loggerFactory.CreateLogger<CircuitBreaker>());

        _shouldFail = true;

        // Act & Assert - Trigger failures to open circuit
        for (int i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _circuitBreaker.ExecuteAsync(SimulateAzureServiceCall));
        }

        // Verify circuit is now open
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
            await _circuitBreaker.ExecuteAsync(SimulateAzureServiceCall));

        _output.WriteLine("Circuit breaker opened after 3 failures as expected");
    }

    /// <summary>
    /// Test: Circuit breaker transitions to half-open state after timeout
    /// Validates: Requirements 6.1
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_TransitionsToHalfOpenAfterTimeout()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(1),
            SuccessThreshold = 1
        };

        _circuitBreaker = new CircuitBreaker(
            Options.Create(options),
            _loggerFactory.CreateLogger<CircuitBreaker>());

        _shouldFail = true;

        // Open the circuit
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _circuitBreaker.ExecuteAsync(SimulateAzureServiceCall));
        }

        // Verify circuit is open
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
            await _circuitBreaker.ExecuteAsync(SimulateAzureServiceCall));

        // Act - Wait for timeout
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // Now service is healthy
        _shouldFail = false;

        // Assert - Should allow test call (half-open state)
        var result = await _circuitBreaker.ExecuteAsync(SimulateAzureServiceCall);
        Assert.Equal("Success", result);

        _output.WriteLine("Circuit breaker transitioned to half-open and closed successfully");
    }

    /// <summary>
    /// Test: Circuit breaker closes after successful recovery
    /// Validates: Requirements 6.1
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_ClosesAfterSuccessfulRecovery()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(1),
            SuccessThreshold = 2
        };

        _circuitBreaker = new CircuitBreaker(
            Options.Create(options),
            _loggerFactory.CreateLogger<CircuitBreaker>());

        _shouldFail = true;

        // Open the circuit
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _circuitBreaker.ExecuteAsync(SimulateAzureServiceCall));
        }

        // Wait for timeout
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // Service is now healthy
        _shouldFail = false;

        // Act - Execute success threshold calls
        for (int i = 0; i < 2; i++)
        {
            var result = await _circuitBreaker.ExecuteAsync(SimulateAzureServiceCall);
            Assert.Equal("Success", result);
        }

        // Assert - Circuit should be fully closed, allowing normal operation
        var finalResult = await _circuitBreaker.ExecuteAsync(SimulateAzureServiceCall);
        Assert.Equal("Success", finalResult);

        _output.WriteLine("Circuit breaker closed after successful recovery");
    }

    /// <summary>
    /// Test: Circuit breaker reopens if failures occur in half-open state
    /// Validates: Requirements 6.1
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_ReopensOnHalfOpenFailure()
    {
        // Arrange
        var options = new CircuitBreakerOptions
        {
            FailureThreshold = 2,
            OpenDuration = TimeSpan.FromSeconds(1),
            SuccessThreshold = 2
        };

        _circuitBreaker = new CircuitBreaker(
            Options.Create(options),
            _loggerFactory.CreateLogger<CircuitBreaker>());

        _shouldFail = true;

        // Open the circuit
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _circuitBreaker.ExecuteAsync(SimulateAzureServiceCall));
        }

        // Wait for timeout to enter half-open
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // Act - Service still failing in half-open state
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _circuitBreaker.ExecuteAsync(SimulateAzureServiceCall));

        // Assert - Circuit should reopen immediately
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(async () =>
            await _circuitBreaker.ExecuteAsync(SimulateAzureServiceCall));

        _output.WriteLine("Circuit breaker reopened after failure in half-open state");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Simulates an Azure service call that can succeed or fail based on test state
    /// </summary>
    private Task<string> SimulateAzureServiceCall()
    {
        _callCount++;
        _output.WriteLine($"Simulated Azure service call #{_callCount}, ShouldFail={_shouldFail}");

        if (_shouldFail)
        {
            throw new InvalidOperationException("Simulated Azure service failure");
        }

        return Task.FromResult("Success");
    }

    #endregion
}
