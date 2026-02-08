using System.Collections.Concurrent;
using System.Diagnostics;

namespace SourceFlow.Cloud.Integration.Tests.TestHelpers;

/// <summary>
/// Utility for measuring performance metrics in cross-cloud tests
/// </summary>
public class PerformanceMeasurement
{
    private readonly ConcurrentBag<TimeSpan> _latencyMeasurements = new();
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly object _lock = new();
    private DateTime _testStartTime;
    private DateTime _testEndTime;
    
    /// <summary>
    /// Start performance measurement
    /// </summary>
    public void StartMeasurement()
    {
        lock (_lock)
        {
            _testStartTime = DateTime.UtcNow;
            _latencyMeasurements.Clear();
            _counters.Clear();
        }
    }
    
    /// <summary>
    /// Stop performance measurement
    /// </summary>
    public void StopMeasurement()
    {
        lock (_lock)
        {
            _testEndTime = DateTime.UtcNow;
        }
    }
    
    /// <summary>
    /// Record a latency measurement
    /// </summary>
    public void RecordLatency(TimeSpan latency)
    {
        _latencyMeasurements.Add(latency);
    }
    
    /// <summary>
    /// Increment a counter
    /// </summary>
    public void IncrementCounter(string counterName, long value = 1)
    {
        _counters.AddOrUpdate(counterName, value, (key, existing) => existing + value);
    }
    
    /// <summary>
    /// Get counter value
    /// </summary>
    public long GetCounter(string counterName)
    {
        return _counters.GetValueOrDefault(counterName, 0);
    }
    
    /// <summary>
    /// Get performance test result
    /// </summary>
    public PerformanceTestResult GetResult(string testName)
    {
        var latencies = _latencyMeasurements.ToArray();
        var duration = _testEndTime - _testStartTime;
        var totalMessages = GetCounter("MessagesProcessed");
        
        return new PerformanceTestResult
        {
            TestName = testName,
            StartTime = _testStartTime,
            EndTime = _testEndTime,
            Duration = duration,
            TotalMessages = (int)totalMessages,
            MessagesPerSecond = duration.TotalSeconds > 0 ? totalMessages / duration.TotalSeconds : 0,
            AverageLatency = latencies.Length > 0 ? TimeSpan.FromTicks((long)latencies.Average(l => l.Ticks)) : TimeSpan.Zero,
            P95Latency = CalculatePercentile(latencies, 0.95),
            P99Latency = CalculatePercentile(latencies, 0.99),
            ResourceUsage = new ResourceUsage
            {
                CpuUsagePercent = GetCounter("CpuUsage"),
                MemoryUsageBytes = GetCounter("MemoryUsage"),
                NetworkBytesIn = GetCounter("NetworkBytesIn"),
                NetworkBytesOut = GetCounter("NetworkBytesOut")
            },
            Errors = GetErrors()
        };
    }
    
    /// <summary>
    /// Record an error
    /// </summary>
    public void RecordError(string error)
    {
        IncrementCounter("Errors");
        _counters.TryAdd($"Error_{DateTime.UtcNow.Ticks}", 1);
    }
    
    /// <summary>
    /// Create a stopwatch for measuring operation latency
    /// </summary>
    public IDisposable MeasureLatency()
    {
        return new LatencyMeasurement(this);
    }
    
    /// <summary>
    /// Calculate percentile from latency measurements
    /// </summary>
    private TimeSpan CalculatePercentile(TimeSpan[] latencies, double percentile)
    {
        if (latencies.Length == 0)
            return TimeSpan.Zero;
        
        var sorted = latencies.OrderBy(l => l.Ticks).ToArray();
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        index = Math.Max(0, Math.Min(index, sorted.Length - 1));
        
        return sorted[index];
    }
    
    /// <summary>
    /// Get list of errors that occurred during testing
    /// </summary>
    private List<string> GetErrors()
    {
        var errors = new List<string>();
        foreach (var kvp in _counters)
        {
            if (kvp.Key.StartsWith("Error_"))
            {
                errors.Add($"Error occurred at {new DateTime(long.Parse(kvp.Key.Substring(6)))}");
            }
        }
        return errors;
    }
    
    /// <summary>
    /// Disposable wrapper for measuring latency
    /// </summary>
    private class LatencyMeasurement : IDisposable
    {
        private readonly PerformanceMeasurement _measurement;
        private readonly Stopwatch _stopwatch;
        
        public LatencyMeasurement(PerformanceMeasurement measurement)
        {
            _measurement = measurement;
            _stopwatch = Stopwatch.StartNew();
        }
        
        public void Dispose()
        {
            _stopwatch.Stop();
            _measurement.RecordLatency(_stopwatch.Elapsed);
        }
    }
}

/// <summary>
/// Performance test result
/// </summary>
public class PerformanceTestResult
{
    /// <summary>
    /// Test name
    /// </summary>
    public string TestName { get; set; } = "";
    
    /// <summary>
    /// Test start time
    /// </summary>
    public DateTime StartTime { get; set; }
    
    /// <summary>
    /// Test end time
    /// </summary>
    public DateTime EndTime { get; set; }
    
    /// <summary>
    /// Test duration
    /// </summary>
    public TimeSpan Duration { get; set; }
    
    /// <summary>
    /// Messages per second throughput
    /// </summary>
    public double MessagesPerSecond { get; set; }
    
    /// <summary>
    /// Total messages processed
    /// </summary>
    public int TotalMessages { get; set; }
    
    /// <summary>
    /// Average latency
    /// </summary>
    public TimeSpan AverageLatency { get; set; }
    
    /// <summary>
    /// 95th percentile latency
    /// </summary>
    public TimeSpan P95Latency { get; set; }
    
    /// <summary>
    /// 99th percentile latency
    /// </summary>
    public TimeSpan P99Latency { get; set; }
    
    /// <summary>
    /// Resource usage during test
    /// </summary>
    public ResourceUsage ResourceUsage { get; set; } = new();
    
    /// <summary>
    /// Errors that occurred during test
    /// </summary>
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Resource usage metrics
/// </summary>
public class ResourceUsage
{
    /// <summary>
    /// CPU usage percentage
    /// </summary>
    public double CpuUsagePercent { get; set; }
    
    /// <summary>
    /// Memory usage in bytes
    /// </summary>
    public long MemoryUsageBytes { get; set; }
    
    /// <summary>
    /// Network bytes received
    /// </summary>
    public long NetworkBytesIn { get; set; }
    
    /// <summary>
    /// Network bytes sent
    /// </summary>
    public long NetworkBytesOut { get; set; }
}