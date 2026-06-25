namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Interface for running Azure-specific performance tests.
/// Provides methods for measuring throughput, latency, auto-scaling, concurrent processing,
/// resource utilization, and session processing performance.
/// </summary>
public interface IAzurePerformanceTestRunner
{
    /// <summary>
    /// Runs a Service Bus throughput test measuring messages per second.
    /// </summary>
    /// <param name="scenario">Test scenario configuration.</param>
    /// <returns>Performance test result with throughput metrics.</returns>
    Task<AzurePerformanceTestResult> RunServiceBusThroughputTestAsync(AzureTestScenario scenario);

    /// <summary>
    /// Runs a Service Bus latency test measuring end-to-end processing times.
    /// </summary>
    /// <param name="scenario">Test scenario configuration.</param>
    /// <returns>Performance test result with latency metrics (P50, P95, P99).</returns>
    Task<AzurePerformanceTestResult> RunServiceBusLatencyTestAsync(AzureTestScenario scenario);

    /// <summary>
    /// Runs an auto-scaling test to validate Service Bus scaling behavior under load.
    /// </summary>
    /// <param name="scenario">Test scenario configuration.</param>
    /// <returns>Performance test result with auto-scaling metrics.</returns>
    Task<AzurePerformanceTestResult> RunAutoScalingTestAsync(AzureTestScenario scenario);

    /// <summary>
    /// Runs a concurrent processing test with multiple senders and receivers.
    /// </summary>
    /// <param name="scenario">Test scenario configuration.</param>
    /// <returns>Performance test result with concurrent processing metrics.</returns>
    Task<AzurePerformanceTestResult> RunConcurrentProcessingTestAsync(AzureTestScenario scenario);

    /// <summary>
    /// Runs a resource utilization test measuring CPU, memory, and network usage.
    /// </summary>
    /// <param name="scenario">Test scenario configuration.</param>
    /// <returns>Performance test result with resource utilization metrics.</returns>
    Task<AzurePerformanceTestResult> RunResourceUtilizationTestAsync(AzureTestScenario scenario);

    /// <summary>
    /// Runs a session processing test measuring session-based message ordering performance.
    /// </summary>
    /// <param name="scenario">Test scenario configuration.</param>
    /// <returns>Performance test result with session processing metrics.</returns>
    Task<AzurePerformanceTestResult> RunSessionProcessingTestAsync(AzureTestScenario scenario);
}

/// <summary>
/// Test scenario configuration for Azure performance tests.
/// </summary>
public class AzureTestScenario
{
    /// <summary>
    /// Name of the test scenario.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Service Bus queue name for the test.
    /// </summary>
    public string QueueName { get; set; } = string.Empty;

    /// <summary>
    /// Service Bus topic name for the test.
    /// </summary>
    public string TopicName { get; set; } = string.Empty;

    /// <summary>
    /// Service Bus subscription name for the test.
    /// </summary>
    public string SubscriptionName { get; set; } = string.Empty;

    /// <summary>
    /// Number of messages to send during the test.
    /// </summary>
    public int MessageCount { get; set; } = 100;

    /// <summary>
    /// Number of concurrent senders.
    /// </summary>
    public int ConcurrentSenders { get; set; } = 1;

    /// <summary>
    /// Number of concurrent receivers.
    /// </summary>
    public int ConcurrentReceivers { get; set; } = 1;

    /// <summary>
    /// Duration of the test.
    /// </summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Size category of messages to send.
    /// </summary>
    public MessageSize MessageSize { get; set; } = MessageSize.Small;

    /// <summary>
    /// Enables session-based message processing.
    /// </summary>
    public bool EnableSessions { get; set; }

    /// <summary>
    /// Enables duplicate detection.
    /// </summary>
    public bool EnableDuplicateDetection { get; set; }

    /// <summary>
    /// Enables message encryption.
    /// </summary>
    public bool EnableEncryption { get; set; }

    /// <summary>
    /// Simulates failures during the test.
    /// </summary>
    public bool SimulateFailures { get; set; }

    /// <summary>
    /// Tests auto-scaling behavior.
    /// </summary>
    public bool TestAutoScaling { get; set; }
}

/// <summary>
/// Message size categories for performance testing.
/// </summary>
public enum MessageSize
{
    /// <summary>
    /// Small messages (less than 1KB).
    /// </summary>
    Small,

    /// <summary>
    /// Medium messages (1KB - 10KB).
    /// </summary>
    Medium,

    /// <summary>
    /// Large messages (10KB - 256KB, Service Bus limit).
    /// </summary>
    Large
}

/// <summary>
/// Result of an Azure performance test.
/// </summary>
public class AzurePerformanceTestResult
{
    /// <summary>
    /// Name of the test.
    /// </summary>
    public string TestName { get; set; } = string.Empty;

    /// <summary>
    /// Start time of the test.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// End time of the test.
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Total duration of the test.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Messages processed per second.
    /// </summary>
    public double MessagesPerSecond { get; set; }

    /// <summary>
    /// Total number of messages sent/received.
    /// </summary>
    public int TotalMessages { get; set; }

    /// <summary>
    /// Number of successfully processed messages.
    /// </summary>
    public int SuccessfulMessages { get; set; }

    /// <summary>
    /// Number of failed messages.
    /// </summary>
    public int FailedMessages { get; set; }

    /// <summary>
    /// Average latency across all messages.
    /// </summary>
    public TimeSpan AverageLatency { get; set; }

    /// <summary>
    /// Median latency (P50).
    /// </summary>
    public TimeSpan MedianLatency { get; set; }

    /// <summary>
    /// 95th percentile latency (P95).
    /// </summary>
    public TimeSpan P95Latency { get; set; }

    /// <summary>
    /// 99th percentile latency (P99).
    /// </summary>
    public TimeSpan P99Latency { get; set; }

    /// <summary>
    /// Minimum latency observed.
    /// </summary>
    public TimeSpan MinLatency { get; set; }

    /// <summary>
    /// Maximum latency observed.
    /// </summary>
    public TimeSpan MaxLatency { get; set; }

    /// <summary>
    /// Service Bus metrics collected during the test.
    /// </summary>
    public ServiceBusMetrics ServiceBusMetrics { get; set; } = new();

    /// <summary>
    /// Auto-scaling metrics (throughput at different load levels).
    /// </summary>
    public List<double> AutoScalingMetrics { get; set; } = new();

    /// <summary>
    /// Scaling efficiency percentage.
    /// </summary>
    public double ScalingEfficiency { get; set; }

    /// <summary>
    /// Resource utilization metrics.
    /// </summary>
    public AzureResourceUsage ResourceUsage { get; set; } = new();

    /// <summary>
    /// Errors encountered during the test.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Custom metrics specific to the test scenario.
    /// </summary>
    public Dictionary<string, object> CustomMetrics { get; set; } = new();
}

/// <summary>
/// Service Bus metrics collected during performance tests.
/// </summary>
public class ServiceBusMetrics
{
    /// <summary>
    /// Number of active messages in the queue/topic.
    /// </summary>
    public long ActiveMessages { get; set; }

    /// <summary>
    /// Number of messages in the dead letter queue.
    /// </summary>
    public long DeadLetterMessages { get; set; }

    /// <summary>
    /// Number of scheduled messages.
    /// </summary>
    public long ScheduledMessages { get; set; }

    /// <summary>
    /// Incoming messages per second.
    /// </summary>
    public double IncomingMessagesPerSecond { get; set; }

    /// <summary>
    /// Outgoing messages per second.
    /// </summary>
    public double OutgoingMessagesPerSecond { get; set; }

    /// <summary>
    /// Number of throttled requests.
    /// </summary>
    public double ThrottledRequests { get; set; }

    /// <summary>
    /// Number of successful requests.
    /// </summary>
    public double SuccessfulRequests { get; set; }

    /// <summary>
    /// Number of failed requests.
    /// </summary>
    public double FailedRequests { get; set; }

    /// <summary>
    /// Average message size in bytes.
    /// </summary>
    public long AverageMessageSizeBytes { get; set; }

    /// <summary>
    /// Average message processing time.
    /// </summary>
    public TimeSpan AverageMessageProcessingTime { get; set; }

    /// <summary>
    /// Number of active connections.
    /// </summary>
    public int ActiveConnections { get; set; }
}

/// <summary>
/// Azure resource utilization metrics.
/// </summary>
public class AzureResourceUsage
{
    /// <summary>
    /// Service Bus CPU utilization percentage.
    /// </summary>
    public double ServiceBusCpuPercent { get; set; }

    /// <summary>
    /// Service Bus memory usage in bytes.
    /// </summary>
    public long ServiceBusMemoryBytes { get; set; }

    /// <summary>
    /// Network bytes received.
    /// </summary>
    public long NetworkBytesIn { get; set; }

    /// <summary>
    /// Network bytes sent.
    /// </summary>
    public long NetworkBytesOut { get; set; }

    /// <summary>
    /// Key Vault requests per second.
    /// </summary>
    public double KeyVaultRequestsPerSecond { get; set; }

    /// <summary>
    /// Key Vault average latency in milliseconds.
    /// </summary>
    public double KeyVaultLatencyMs { get; set; }

    /// <summary>
    /// Number of Service Bus connections.
    /// </summary>
    public int ServiceBusConnectionCount { get; set; }

    /// <summary>
    /// Service Bus namespace utilization percentage.
    /// </summary>
    public double ServiceBusNamespaceUtilizationPercent { get; set; }
}
