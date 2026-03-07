using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using FsCheck;
using FsCheck.Xunit;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Bug condition exploration tests for LocalStack timeout and port conflicts in GitHub Actions CI
/// 
/// **CRITICAL**: These tests are EXPECTED TO FAIL on unfixed code - failure confirms the bug exists
/// **DO NOT attempt to fix the test or the code when it fails**
/// **NOTE**: These tests encode the expected behavior - they will validate the fix when they pass after implementation
/// **GOAL**: Surface counterexamples that demonstrate the bug exists in GitHub Actions CI
/// 
/// Bug Condition: LocalStack containers in GitHub Actions CI do not report all services "available" 
/// within 30-second timeout, and parallel test execution causes port conflicts.
/// 
/// Expected Outcome: Tests FAIL with timeout after 30 seconds or port conflicts (this proves the bug exists)
/// 
/// Validates: Requirements 1.1, 1.2, 1.3, 1.4, 1.5 from bugfix.md
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
[Trait("Category", "BugExploration")]
[Collection("AWS Integration Tests")]
public class LocalStackCITimeoutExplorationTests : IAsyncLifetime
{
    private readonly ILogger<LocalStackCITimeoutExplorationTests> _logger;
    private LocalStackManager? _localStackManager;
    private readonly List<string> _counterexamples = new();
    private readonly Stopwatch _stopwatch = new();
    
    public LocalStackCITimeoutExplorationTests()
    {
        var loggerFactory = LoggerFactory.Create(builder => 
            builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _logger = loggerFactory.CreateLogger<LocalStackCITimeoutExplorationTests>();
    }
    
    public Task InitializeAsync()
    {
        _localStackManager = new LocalStackManager(
            LoggerFactory.Create(builder => 
                builder.AddConsole().SetMinimumLevel(LogLevel.Debug))
            .CreateLogger<LocalStackManager>());
        return Task.CompletedTask;
    }
    
    public async Task DisposeAsync()
    {
        if (_localStackManager != null)
        {
            await _localStackManager.DisposeAsync();
        }
        
        // Log all counterexamples found during test execution
        if (_counterexamples.Any())
        {
            _logger.LogWarning("=== COUNTEREXAMPLES FOUND ===");
            foreach (var counterexample in _counterexamples)
            {
                _logger.LogWarning(counterexample);
            }
            _logger.LogWarning("=== END COUNTEREXAMPLES ===");
        }
    }
    
    /// <summary>
    /// **Validates: Requirements 1.1, 1.3, 1.5**
    /// 
    /// Property 1: Fault Condition - LocalStack Services Ready in CI
    /// 
    /// Tests that LocalStack containers in GitHub Actions CI report all services "available" within 90 seconds.
    /// 
    /// **EXPECTED OUTCOME ON UNFIXED CODE**: 
    /// - Test FAILS with TimeoutException after 30 seconds
    /// - Services still report "initializing" status when timeout occurs
    /// - Counterexample documents actual time required for services to become "available" in CI
    /// 
    /// **EXPECTED OUTCOME AFTER FIX**: 
    /// - Test PASSES with all services reporting "available" within 90 seconds
    /// - Enhanced retry logic and CI-specific timeouts allow sufficient initialization time
    /// </summary>
    [Fact]
    public async Task LocalStack_ServicesReady_WithinCITimeout()
    {
        // Scoped PBT: Focus on the concrete failing case in CI environment
        // This property is scoped to test the specific bug condition
        
        // Detect if we're running in GitHub Actions CI
        var isGitHubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        
        if (!isGitHubActions)
        {
            // Skip this test in local development - it's designed for CI
            _logger.LogInformation("Skipping CI-specific test in local environment");
            return;
        }
        
        _logger.LogInformation("=== BUG EXPLORATION TEST: LocalStack CI Timeout ===");
        var services = new[] { "sqs", "sns", "kms", "iam" };
        _logger.LogInformation("Testing services: {Services}", string.Join(", ", services));
        
        // Use UNFIXED configuration (30-second timeout from current code)
        var config = TestHelpers.LocalStackConfiguration.CreateForIntegrationTesting();
        
        // Document the current timeout configuration
        _logger.LogInformation("Current configuration:");
        _logger.LogInformation("  HealthCheckTimeout: {Timeout}", config.HealthCheckTimeout);
        _logger.LogInformation("  MaxHealthCheckRetries: {Retries}", config.MaxHealthCheckRetries);
        _logger.LogInformation("  HealthCheckRetryDelay: {Delay}", config.HealthCheckRetryDelay);
        
        _stopwatch.Restart();
        
        try
        {
            // Attempt to start LocalStack with current (unfixed) configuration
            await _localStackManager!.StartAsync(config);
            
            _stopwatch.Stop();
            var elapsedTime = _stopwatch.Elapsed;
            
            // If we get here, services became ready
            _logger.LogInformation("Services became ready after {ElapsedTime}", elapsedTime);
            
            // Check individual service ready times
            var healthStatus = await _localStackManager.GetServicesHealthAsync();
            foreach (var service in services)
            {
                if (healthStatus.TryGetValue(service, out var health))
                {
                    _logger.LogInformation("Service {Service}: Status={Status}, ResponseTime={ResponseTime}ms",
                        service, health.Status, health.ResponseTime.TotalMilliseconds);
                }
            }
            
            // Expected behavior: All services should be available within 90 seconds
            // On unfixed code, this will likely timeout at 30 seconds
            var allAvailable = healthStatus.Values.All(h => h.IsAvailable);
            
            if (!allAvailable)
            {
                var counterexample = $"COUNTEREXAMPLE: Services not all available after {elapsedTime}. " +
                    $"Status: {string.Join(", ", healthStatus.Select(kvp => $"{kvp.Key}={kvp.Value.Status}"))}";
                _counterexamples.Add(counterexample);
                _logger.LogWarning(counterexample);
            }
            
            Assert.True(allAvailable, 
                $"Expected all services to be available. " +
                $"Status: {string.Join(", ", healthStatus.Select(kvp => $"{kvp.Key}={kvp.Value.Status}"))}");
        }
        catch (TimeoutException ex)
        {
            _stopwatch.Stop();
            var elapsedTime = _stopwatch.Elapsed;
            
            // This is the EXPECTED outcome on unfixed code
            var counterexample = $"COUNTEREXAMPLE: Timeout after {elapsedTime}. " +
                $"Message: {ex.Message}. " +
                $"This confirms the bug - services need more than {config.HealthCheckTimeout} to become ready in CI.";
            _counterexamples.Add(counterexample);
            _logger.LogWarning(counterexample);
            
            // Try to get service status at time of failure
            try
            {
                var healthStatus = await _localStackManager!.GetServicesHealthAsync();
                var statusDetails = string.Join(", ", 
                    healthStatus.Select(kvp => $"{kvp.Key}={kvp.Value.Status}"));
                _logger.LogWarning("Service status at timeout: {Status}", statusDetails);
                _counterexamples.Add($"Service status at timeout: {statusDetails}");
            }
            catch (Exception healthEx)
            {
                _logger.LogWarning("Could not retrieve service status: {Error}", healthEx.Message);
            }
            
            // Throw to fail the test (this confirms the bug exists)
            throw new Exception(counterexample, ex);
        }
        catch (Exception ex)
        {
            _stopwatch.Stop();
            var counterexample = $"COUNTEREXAMPLE: Unexpected error after {_stopwatch.Elapsed}: {ex.Message}";
            _counterexamples.Add(counterexample);
            _logger.LogError(ex, counterexample);
            throw new Exception(counterexample, ex);
        }
    }
    
    /// <summary>
    /// **Validates: Requirements 1.2, 1.4**
    /// 
    /// Property 2: Fault Condition - External Instance Detection
    /// 
    /// Tests that external LocalStack instances are detected within 10 seconds with retry logic.
    /// 
    /// **EXPECTED OUTCOME ON UNFIXED CODE**: 
    /// - Test FAILS because external instance detection timeout is only 3 seconds
    /// - No retry logic exists for detection
    /// - Counterexample documents detection failures within 3-second timeout
    /// 
    /// **EXPECTED OUTCOME AFTER FIX**: 
    /// - Test PASSES with external instances detected within 10 seconds
    /// - Retry logic (3 attempts with 2-second delays) improves detection reliability
    /// </summary>
    [Fact]
    public async Task LocalStack_ExternalInstanceDetection_WithinTimeout()
    {
        var isGitHubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        
        if (!isGitHubActions)
        {
            _logger.LogInformation("Skipping CI-specific test in local environment");
            return;
        }
        
        _logger.LogInformation("=== BUG EXPLORATION TEST: External Instance Detection ===");
        
        // Check if there's an external LocalStack instance (e.g., pre-started in GitHub Actions)
        var config = TestHelpers.LocalStackConfiguration.CreateForIntegrationTesting();
        
        _stopwatch.Restart();
        
        try
        {
            // This will use the current (unfixed) 3-second timeout for external detection
            await _localStackManager!.StartAsync(config);
            
            _stopwatch.Stop();
            
            _logger.LogInformation("LocalStack started/detected after {ElapsedTime}", _stopwatch.Elapsed);
            
            // Check if it detected an external instance or started a new one
            var healthStatus = await _localStackManager.GetServicesHealthAsync();
            var allAvailable = healthStatus.Values.All(h => h.IsAvailable);
            
            Assert.True(allAvailable, 
                "Expected all services to be available. " +
                $"Status: {string.Join(", ", healthStatus.Select(kvp => $"{kvp.Key}={kvp.Value.Status}"))}");
        }
        catch (TimeoutException ex)
        {
            _stopwatch.Stop();
            
            var counterexample = $"COUNTEREXAMPLE: External instance detection failed after {_stopwatch.Elapsed}. " +
                $"Message: {ex.Message}. " +
                $"Current timeout is 3 seconds, which may be insufficient for CI environments.";
            _counterexamples.Add(counterexample);
            _logger.LogWarning(counterexample);
            
            // This failure confirms the bug exists
            throw new Exception(counterexample, ex);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("port is already allocated"))
        {
            _stopwatch.Stop();
            
            var counterexample = $"COUNTEREXAMPLE: Port conflict detected after {_stopwatch.Elapsed}. " +
                $"Message: {ex.Message}. " +
                $"This indicates external instance detection failed and a new container was attempted.";
            _counterexamples.Add(counterexample);
            _logger.LogWarning(counterexample);
            
            // This failure confirms the bug exists
            throw new Exception(counterexample, ex);
        }
    }
    
    /// <summary>
    /// **Validates: Requirements 1.1, 1.3, 1.5**
    /// 
    /// Property 3: Fault Condition - Individual Service Timing
    /// 
    /// Tests and documents the actual time required for each service to become "available" in CI.
    /// This is a diagnostic test to gather data about service initialization times.
    /// 
    /// **EXPECTED OUTCOME ON UNFIXED CODE**: 
    /// - Test FAILS with timeout after 30 seconds
    /// - Logs show which services became ready and which didn't
    /// - Counterexample documents actual timing for each service (e.g., SQS: 25s, KMS: 45s)
    /// 
    /// **EXPECTED OUTCOME AFTER FIX**: 
    /// - Test PASSES with all services ready within 90 seconds
    /// - Logs show actual initialization times for each service
    /// </summary>
    [Fact]
    public async Task LocalStack_ServiceTiming_DocumentActualInitializationTimes()
    {
        var isGitHubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        
        if (!isGitHubActions)
        {
            _logger.LogInformation("Skipping CI-specific test in local environment");
            return;
        }
        
        _logger.LogInformation("=== BUG EXPLORATION TEST: Service Timing Analysis ===");
        
        var config = TestHelpers.LocalStackConfiguration.CreateForIntegrationTesting();
        var services = config.EnabledServices.ToArray();
        
        _logger.LogInformation("Monitoring initialization times for services: {Services}", 
            string.Join(", ", services));
        
        var serviceTimings = new Dictionary<string, TimeSpan?>();
        foreach (var service in services)
        {
            serviceTimings[service] = null;
        }
        
        _stopwatch.Restart();
        var startTime = DateTime.UtcNow;
        
        try
        {
            await _localStackManager!.StartAsync(config);
            
            _stopwatch.Stop();
            
            // Get final health status
            var healthStatus = await _localStackManager.GetServicesHealthAsync();
            
            _logger.LogInformation("=== SERVICE TIMING RESULTS ===");
            _logger.LogInformation("Total startup time: {TotalTime}", _stopwatch.Elapsed);
            
            foreach (var service in services)
            {
                if (healthStatus.TryGetValue(service, out var health))
                {
                    var timing = health.LastChecked - startTime;
                    serviceTimings[service] = timing;
                    
                    _logger.LogInformation("Service {Service}: Status={Status}, Time={Time}, ResponseTime={ResponseTime}ms",
                        service, health.Status, timing, health.ResponseTime.TotalMilliseconds);
                }
                else
                {
                    _logger.LogWarning("Service {Service}: NOT FOUND in health status", service);
                }
            }
            
            // Check if all services are available
            var allAvailable = healthStatus.Values.All(h => h.IsAvailable);
            
            if (!allAvailable)
            {
                var notAvailable = healthStatus.Where(kvp => !kvp.Value.IsAvailable)
                    .Select(kvp => $"{kvp.Key}={kvp.Value.Status}");
                var counterexample = $"COUNTEREXAMPLE: Not all services available after {_stopwatch.Elapsed}. " +
                    $"Not available: {string.Join(", ", notAvailable)}";
                _counterexamples.Add(counterexample);
                _logger.LogWarning(counterexample);
            }
            
            Assert.True(allAvailable, 
                $"Expected all services to be available within timeout. " +
                $"Timings: {string.Join(", ", serviceTimings.Select(kvp => $"{kvp.Key}={kvp.Value?.TotalSeconds:F1}s"))}");
        }
        catch (TimeoutException ex)
        {
            _stopwatch.Stop();
            
            // Document which services became ready and which didn't
            try
            {
                var healthStatus = await _localStackManager!.GetServicesHealthAsync();
                
                _logger.LogWarning("=== SERVICE TIMING AT TIMEOUT ===");
                _logger.LogWarning("Timeout occurred after: {ElapsedTime}", _stopwatch.Elapsed);
                
                foreach (var service in services)
                {
                    if (healthStatus.TryGetValue(service, out var health))
                    {
                        var timing = health.LastChecked - startTime;
                        serviceTimings[service] = timing;
                        
                        _logger.LogWarning("Service {Service}: Status={Status}, Time={Time}",
                            service, health.Status, timing);
                    }
                    else
                    {
                        _logger.LogWarning("Service {Service}: NO STATUS AVAILABLE", service);
                    }
                }
            }
            catch (Exception healthEx)
            {
                _logger.LogWarning("Could not retrieve service status: {Error}", healthEx.Message);
            }
            
            var counterexample = $"COUNTEREXAMPLE: Timeout after {_stopwatch.Elapsed}. " +
                $"Message: {ex.Message}. " +
                $"Service timings: {string.Join(", ", serviceTimings.Select(kvp => $"{kvp.Key}={kvp.Value?.TotalSeconds.ToString("F1") ?? "N/A"}s"))}";
            _counterexamples.Add(counterexample);
            _logger.LogWarning(counterexample);
            
            throw new Exception(counterexample, ex);
        }
    }
}
