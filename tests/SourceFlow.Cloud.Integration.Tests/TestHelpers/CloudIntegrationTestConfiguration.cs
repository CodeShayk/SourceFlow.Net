using Amazon;

namespace SourceFlow.Cloud.Integration.Tests.TestHelpers;

/// <summary>
/// Configuration for cross-cloud integration tests
/// </summary>
public class CloudIntegrationTestConfiguration
{
    /// <summary>
    /// Whether to use emulators for testing
    /// </summary>
    public bool UseEmulators { get; set; } = true;
    
    /// <summary>
    /// Whether to run integration tests
    /// </summary>
    public bool RunIntegrationTests { get; set; } = true;
    
    /// <summary>
    /// Whether to run performance tests
    /// </summary>
    public bool RunPerformanceTests { get; set; } = false;
    
    /// <summary>
    /// Whether to run security tests
    /// </summary>
    public bool RunSecurityTests { get; set; } = true;
    
    /// <summary>
    /// Test execution timeout
    /// </summary>
    public TimeSpan TestTimeout { get; set; } = TimeSpan.FromMinutes(5);
    
    /// <summary>
    /// AWS test configuration
    /// </summary>
    public AwsIntegrationTestConfiguration Aws { get; set; } = new();
    
    /// <summary>
    /// Azure test configuration
    /// </summary>
    public AzureIntegrationTestConfiguration Azure { get; set; } = new();
    
    /// <summary>
    /// Performance test configuration
    /// </summary>
    public PerformanceTestConfiguration Performance { get; set; } = new();
    
    /// <summary>
    /// Security test configuration
    /// </summary>
    public SecurityTestConfiguration Security { get; set; } = new();
}

/// <summary>
/// AWS-specific integration test configuration
/// </summary>
public class AwsIntegrationTestConfiguration
{
    /// <summary>
    /// Whether to use LocalStack emulator
    /// </summary>
    public bool UseLocalStack { get; set; } = true;
    
    /// <summary>
    /// LocalStack endpoint URL
    /// </summary>
    public string LocalStackEndpoint { get; set; } = "http://localhost:4566";
    
    /// <summary>
    /// AWS region for testing
    /// </summary>
    public string Region { get; set; } = "us-east-1";
    
    /// <summary>
    /// AWS access key for testing (used with LocalStack)
    /// </summary>
    public string AccessKey { get; set; } = "test";
    
    /// <summary>
    /// AWS secret key for testing (used with LocalStack)
    /// </summary>
    public string SecretKey { get; set; } = "test";
    
    /// <summary>
    /// Whether to run AWS integration tests
    /// </summary>
    public bool RunIntegrationTests { get; set; } = true;
    
    /// <summary>
    /// Whether to run AWS performance tests
    /// </summary>
    public bool RunPerformanceTests { get; set; } = false;
    
    /// <summary>
    /// Command routing configuration
    /// </summary>
    public Dictionary<string, string> CommandRouting { get; set; } = new();
    
    /// <summary>
    /// Event routing configuration
    /// </summary>
    public Dictionary<string, string> EventRouting { get; set; } = new();
    
    /// <summary>
    /// KMS key ID for encryption tests
    /// </summary>
    public string? KmsKeyId { get; set; }
}

/// <summary>
/// Azure-specific integration test configuration
/// </summary>
public class AzureIntegrationTestConfiguration
{
    /// <summary>
    /// Whether to use Azurite emulator
    /// </summary>
    public bool UseAzurite { get; set; } = true;
    
    /// <summary>
    /// Service Bus connection string
    /// </summary>
    public string ServiceBusConnectionString { get; set; } = "";
    
    /// <summary>
    /// Service Bus fully qualified namespace
    /// </summary>
    public string FullyQualifiedNamespace { get; set; } = "test.servicebus.windows.net";
    
    /// <summary>
    /// Whether to use managed identity
    /// </summary>
    public bool UseManagedIdentity { get; set; } = false;
    
    /// <summary>
    /// Whether to run Azure integration tests
    /// </summary>
    public bool RunIntegrationTests { get; set; } = true;
    
    /// <summary>
    /// Whether to run Azure performance tests
    /// </summary>
    public bool RunPerformanceTests { get; set; } = false;
    
    /// <summary>
    /// Command routing configuration
    /// </summary>
    public Dictionary<string, string> CommandRouting { get; set; } = new();
    
    /// <summary>
    /// Event routing configuration
    /// </summary>
    public Dictionary<string, AzureEventRoutingConfig> EventRouting { get; set; } = new();
    
    /// <summary>
    /// Key Vault URI for encryption tests
    /// </summary>
    public string? KeyVaultUri { get; set; }
}

/// <summary>
/// Azure event routing configuration
/// </summary>
public class AzureEventRoutingConfig
{
    /// <summary>
    /// Service Bus topic name
    /// </summary>
    public string TopicName { get; set; } = "";
    
    /// <summary>
    /// Service Bus subscription name
    /// </summary>
    public string SubscriptionName { get; set; } = "";
}

/// <summary>
/// Performance test configuration
/// </summary>
public class PerformanceTestConfiguration
{
    /// <summary>
    /// Throughput test configuration
    /// </summary>
    public ThroughputTestConfig ThroughputTest { get; set; } = new();
    
    /// <summary>
    /// Latency test configuration
    /// </summary>
    public LatencyTestConfig LatencyTest { get; set; } = new();
    
    /// <summary>
    /// Scalability test configuration
    /// </summary>
    public ScalabilityTestConfig ScalabilityTest { get; set; } = new();
}

/// <summary>
/// Throughput test configuration
/// </summary>
public class ThroughputTestConfig
{
    /// <summary>
    /// Number of messages to send
    /// </summary>
    public int MessageCount { get; set; } = 1000;
    
    /// <summary>
    /// Number of concurrent senders
    /// </summary>
    public int ConcurrentSenders { get; set; } = 5;
    
    /// <summary>
    /// Test duration
    /// </summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Latency test configuration
/// </summary>
public class LatencyTestConfig
{
    /// <summary>
    /// Number of messages to send
    /// </summary>
    public int MessageCount { get; set; } = 100;
    
    /// <summary>
    /// Number of concurrent senders
    /// </summary>
    public int ConcurrentSenders { get; set; } = 1;
    
    /// <summary>
    /// Number of warmup messages
    /// </summary>
    public int WarmupMessages { get; set; } = 10;
}

/// <summary>
/// Scalability test configuration
/// </summary>
public class ScalabilityTestConfig
{
    /// <summary>
    /// Minimum concurrency level
    /// </summary>
    public int MinConcurrency { get; set; } = 1;
    
    /// <summary>
    /// Maximum concurrency level
    /// </summary>
    public int MaxConcurrency { get; set; } = 20;
    
    /// <summary>
    /// Concurrency step size
    /// </summary>
    public int StepSize { get; set; } = 5;
    
    /// <summary>
    /// Messages per concurrency step
    /// </summary>
    public int MessagesPerStep { get; set; } = 500;
}

/// <summary>
/// Security test configuration
/// </summary>
public class SecurityTestConfiguration
{
    /// <summary>
    /// Encryption test configuration
    /// </summary>
    public EncryptionTestConfig EncryptionTest { get; set; } = new();
    
    /// <summary>
    /// Access control test configuration
    /// </summary>
    public AccessControlTestConfig AccessControlTest { get; set; } = new();
}

/// <summary>
/// Encryption test configuration
/// </summary>
public class EncryptionTestConfig
{
    /// <summary>
    /// Whether to test sensitive data handling
    /// </summary>
    public bool TestSensitiveData { get; set; } = true;
    
    /// <summary>
    /// Whether to test key rotation
    /// </summary>
    public bool TestKeyRotation { get; set; } = false;
    
    /// <summary>
    /// Whether to validate data masking
    /// </summary>
    public bool ValidateDataMasking { get; set; } = true;
}

/// <summary>
/// Access control test configuration
/// </summary>
public class AccessControlTestConfig
{
    /// <summary>
    /// Whether to test invalid credentials
    /// </summary>
    public bool TestInvalidCredentials { get; set; } = true;
    
    /// <summary>
    /// Whether to test insufficient permissions
    /// </summary>
    public bool TestInsufficientPermissions { get; set; } = true;
    
    /// <summary>
    /// Whether to test cross-cloud access
    /// </summary>
    public bool TestCrossCloudAccess { get; set; } = true;
}