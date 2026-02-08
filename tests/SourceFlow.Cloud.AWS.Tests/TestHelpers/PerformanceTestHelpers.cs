using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Helper class for performance testing
/// </summary>
public static class PerformanceTestHelpers
{
    /// <summary>
    /// Measure execution time of an async operation
    /// </summary>
    public static async Task<TimeSpan> MeasureAsync(Func<Task> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        await operation();
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }
    
    /// <summary>
    /// Measure execution time of an async operation with result
    /// </summary>
    public static async Task<(T Result, TimeSpan Duration)> MeasureAsync<T>(Func<Task<T>> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await operation();
        stopwatch.Stop();
        return (result, stopwatch.Elapsed);
    }
    
    /// <summary>
    /// Run a performance test with multiple iterations
    /// </summary>
    public static async Task<PerformanceTestResult> RunPerformanceTestAsync(
        string testName,
        Func<Task> operation,
        int iterations = 100,
        int warmupIterations = 10)
    {
        var durations = new List<TimeSpan>();
        
        // Warmup
        for (int i = 0; i < warmupIterations; i++)
        {
            await operation();
        }
        
        // Actual test
        var totalStopwatch = Stopwatch.StartNew();
        
        for (int i = 0; i < iterations; i++)
        {
            var duration = await MeasureAsync(operation);
            durations.Add(duration);
        }
        
        totalStopwatch.Stop();
        
        return new PerformanceTestResult
        {
            TestName = testName,
            Iterations = iterations,
            TotalDuration = totalStopwatch.Elapsed,
            AverageDuration = TimeSpan.FromTicks(durations.Sum(d => d.Ticks) / durations.Count),
            MinDuration = durations.Min(),
            MaxDuration = durations.Max(),
            P95Duration = durations.OrderBy(d => d).Skip((int)(durations.Count * 0.95)).First(),
            P99Duration = durations.OrderBy(d => d).Skip((int)(durations.Count * 0.99)).First(),
            OperationsPerSecond = iterations / totalStopwatch.Elapsed.TotalSeconds
        };
    }
    
    /// <summary>
    /// Run BenchmarkDotNet performance tests
    /// </summary>
    public static void RunBenchmark<T>() where T : class
    {
        BenchmarkRunner.Run<T>();
    }
}

/// <summary>
/// Result of a performance test
/// </summary>
public class PerformanceTestResult
{
    public string TestName { get; set; } = "";
    public int Iterations { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public TimeSpan AverageDuration { get; set; }
    public TimeSpan MinDuration { get; set; }
    public TimeSpan MaxDuration { get; set; }
    public TimeSpan P95Duration { get; set; }
    public TimeSpan P99Duration { get; set; }
    public double OperationsPerSecond { get; set; }
    
    public override string ToString()
    {
        return $"{TestName}: {OperationsPerSecond:F2} ops/sec, Avg: {AverageDuration.TotalMilliseconds:F2}ms, P95: {P95Duration.TotalMilliseconds:F2}ms";
    }
}

/// <summary>
/// Base class for BenchmarkDotNet performance tests
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public abstract class PerformanceBenchmarkBase
{
    protected LocalStackTestFixture? LocalStack { get; private set; }
    
    [GlobalSetup]
    public virtual async Task GlobalSetup()
    {
        LocalStack = new LocalStackTestFixture();
        await LocalStack.InitializeAsync();
    }
    
    [GlobalCleanup]
    public virtual async Task GlobalCleanup()
    {
        if (LocalStack != null)
        {
            await LocalStack.DisposeAsync();
        }
    }
}