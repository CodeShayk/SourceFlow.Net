using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.Integration.Tests.TestHelpers;

/// <summary>
/// Test command for cross-cloud integration testing
/// </summary>
public class CrossCloudTestCommand : ICommand
{
    public IPayload Payload { get; set; } = null!;
    public EntityRef Entity { get; set; } = null!;
    public string Name { get; set; } = null!;
    public Metadata Metadata { get; set; } = null!;
}

/// <summary>
/// Test command payload for cross-cloud scenarios
/// </summary>
public class CrossCloudTestPayload : IPayload
{
    /// <summary>
    /// Test message content
    /// </summary>
    public string Message { get; set; } = "";
    
    /// <summary>
    /// Source cloud provider
    /// </summary>
    public string SourceCloud { get; set; } = "";
    
    /// <summary>
    /// Destination cloud provider
    /// </summary>
    public string DestinationCloud { get; set; } = "";
    
    /// <summary>
    /// Test scenario identifier
    /// </summary>
    public string ScenarioId { get; set; } = "";
    
    /// <summary>
    /// Timestamp when command was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Test event for cross-cloud integration testing
/// </summary>
public class CrossCloudTestEvent : IEvent
{
    public string Name { get; set; } = null!;
    public IEntity Payload { get; set; } = null!;
    public Metadata Metadata { get; set; } = null!;
}

/// <summary>
/// Test event payload for cross-cloud scenarios
/// </summary>
public class CrossCloudTestEventPayload : IEntity
{
    /// <summary>
    /// Entity ID
    /// </summary>
    public int Id { get; set; }
    
    /// <summary>
    /// Test result message
    /// </summary>
    public string ResultMessage { get; set; } = "";
    
    /// <summary>
    /// Source cloud provider
    /// </summary>
    public string SourceCloud { get; set; } = "";
    
    /// <summary>
    /// Processing cloud provider
    /// </summary>
    public string ProcessingCloud { get; set; } = "";
    
    /// <summary>
    /// Test scenario identifier
    /// </summary>
    public string ScenarioId { get; set; } = "";
    
    /// <summary>
    /// Processing timestamp
    /// </summary>
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Whether the test was successful
    /// </summary>
    public bool Success { get; set; }
}

/// <summary>
/// AWS to Azure test command
/// </summary>
public class AwsToAzureCommand : ICommand
{
    public IPayload Payload { get; set; } = null!;
    public EntityRef Entity { get; set; } = null!;
    public string Name { get; set; } = "AwsToAzureCommand";
    public Metadata Metadata { get; set; } = null!;
}

/// <summary>
/// Azure to AWS test command
/// </summary>
public class AzureToAwsCommand : ICommand
{
    public IPayload Payload { get; set; } = null!;
    public EntityRef Entity { get; set; } = null!;
    public string Name { get; set; } = "AzureToAwsCommand";
    public Metadata Metadata { get; set; } = null!;
}

/// <summary>
/// Failover test command
/// </summary>
public class FailoverTestCommand : ICommand
{
    public IPayload Payload { get; set; } = null!;
    public EntityRef Entity { get; set; } = null!;
    public string Name { get; set; } = "FailoverTestCommand";
    public Metadata Metadata { get; set; } = null!;
}

/// <summary>
/// AWS to Azure test event
/// </summary>
public class AwsToAzureEvent : IEvent
{
    public string Name { get; set; } = "AwsToAzureEvent";
    public IEntity Payload { get; set; } = null!;
    public Metadata Metadata { get; set; } = null!;
}

/// <summary>
/// Azure to AWS test event
/// </summary>
public class AzureToAwsEvent : IEvent
{
    public string Name { get; set; } = "AzureToAwsEvent";
    public IEntity Payload { get; set; } = null!;
    public Metadata Metadata { get; set; } = null!;
}

/// <summary>
/// Failover test event
/// </summary>
public class FailoverTestEvent : IEvent
{
    public string Name { get; set; } = "FailoverTestEvent";
    public IEntity Payload { get; set; } = null!;
    public Metadata Metadata { get; set; } = null!;
}

/// <summary>
/// Test metadata for cross-cloud scenarios
/// </summary>
public class CrossCloudTestMetadata : Metadata
{
    /// <summary>
    /// Correlation ID for tracking messages across clouds
    /// </summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    
    /// <summary>
    /// Test execution ID
    /// </summary>
    public string TestExecutionId { get; set; } = "";
    
    /// <summary>
    /// Source cloud provider
    /// </summary>
    public string SourceCloud { get; set; } = "";
    
    /// <summary>
    /// Target cloud provider
    /// </summary>
    public string TargetCloud { get; set; } = "";
    
    /// <summary>
    /// Test scenario type
    /// </summary>
    public string ScenarioType { get; set; } = "";
    
    public CrossCloudTestMetadata()
    {
    }
}

/// <summary>
/// Cross-cloud test result
/// </summary>
public class CrossCloudTestResult
{
    /// <summary>
    /// Source cloud provider
    /// </summary>
    public string SourceCloud { get; set; } = "";
    
    /// <summary>
    /// Destination cloud provider
    /// </summary>
    public string DestinationCloud { get; set; } = "";
    
    /// <summary>
    /// Whether the test was successful
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// End-to-end latency
    /// </summary>
    public TimeSpan EndToEndLatency { get; set; }
    
    /// <summary>
    /// Message path through the system
    /// </summary>
    public List<string> MessagePath { get; set; } = new();
    
    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
    
    /// <summary>
    /// Error message if test failed
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// Test execution timestamp
    /// </summary>
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Test scenario definition
/// </summary>
public class TestScenario
{
    /// <summary>
    /// Scenario name
    /// </summary>
    public string Name { get; set; } = "";
    
    /// <summary>
    /// Source cloud provider
    /// </summary>
    public CloudProvider SourceProvider { get; set; }
    
    /// <summary>
    /// Destination cloud provider
    /// </summary>
    public CloudProvider DestinationProvider { get; set; }
    
    /// <summary>
    /// Number of messages to send
    /// </summary>
    public int MessageCount { get; set; } = 100;
    
    /// <summary>
    /// Number of concurrent senders
    /// </summary>
    public int ConcurrentSenders { get; set; } = 1;
    
    /// <summary>
    /// Test duration
    /// </summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(1);
    
    /// <summary>
    /// Message size category
    /// </summary>
    public MessageSize MessageSize { get; set; } = MessageSize.Small;
    
    /// <summary>
    /// Whether to enable encryption
    /// </summary>
    public bool EnableEncryption { get; set; } = false;
    
    /// <summary>
    /// Whether to simulate failures
    /// </summary>
    public bool SimulateFailures { get; set; } = false;
}

/// <summary>
/// Cloud provider enumeration
/// </summary>
public enum CloudProvider
{
    Local,
    AWS,
    Azure,
    Hybrid
}

/// <summary>
/// Message size categories
/// </summary>
public enum MessageSize
{
    Small,    // < 1KB
    Medium,   // 1KB - 10KB
    Large     // 10KB - 256KB
}

/// <summary>
/// Hybrid test scenario for property testing
/// </summary>
public class HybridTestScenario
{
    /// <summary>
    /// Number of messages to process
    /// </summary>
    public int MessageCount { get; set; }
    
    /// <summary>
    /// Whether to use local processing
    /// </summary>
    public bool UseLocalProcessing { get; set; }
    
    /// <summary>
    /// Cloud provider for the scenario
    /// </summary>
    public CloudProvider CloudProvider { get; set; }
    
    /// <summary>
    /// Message size for the test
    /// </summary>
    public MessageSize MessageSize { get; set; } = MessageSize.Small;
}