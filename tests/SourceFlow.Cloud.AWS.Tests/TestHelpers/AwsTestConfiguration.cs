using Amazon;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Enhanced configuration for AWS integration tests
/// </summary>
public class AwsTestConfiguration
{
    /// <summary>
    /// AWS region for testing
    /// </summary>
    public RegionEndpoint Region { get; set; } = RegionEndpoint.USEast1;
    
    /// <summary>
    /// Whether to use LocalStack emulator
    /// </summary>
    public bool UseLocalStack { get; set; } = true;
    
    /// <summary>
    /// LocalStack endpoint URL
    /// </summary>
    public string LocalStackEndpoint { get; set; } = "http://localhost:4566";
    
    /// <summary>
    /// AWS access key for testing (used with LocalStack)
    /// </summary>
    public string AccessKey { get; set; } = "test";
    
    /// <summary>
    /// AWS secret key for testing (used with LocalStack)
    /// </summary>
    public string SecretKey { get; set; } = "test";
    
    /// <summary>
    /// Test queue URLs mapped by command type
    /// </summary>
    public Dictionary<string, string> QueueUrls { get; set; } = new();
    
    /// <summary>
    /// Test topic ARNs mapped by event type
    /// </summary>
    public Dictionary<string, string> TopicArns { get; set; } = new();
    
    /// <summary>
    /// Whether to run integration tests (requires AWS services or LocalStack)
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
    /// KMS key ID for encryption tests
    /// </summary>
    public string? KmsKeyId { get; set; }
    
    /// <summary>
    /// LocalStack configuration
    /// </summary>
    public LocalStackConfiguration LocalStack { get; set; } = new();
    
    /// <summary>
    /// AWS service configurations
    /// </summary>
    public AwsServiceConfiguration Services { get; set; } = new();
    
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
/// AWS service-specific configurations
/// </summary>
public class AwsServiceConfiguration
{
    /// <summary>
    /// SQS configuration
    /// </summary>
    public SqsConfiguration Sqs { get; set; } = new();
    
    /// <summary>
    /// SNS configuration
    /// </summary>
    public SnsConfiguration Sns { get; set; } = new();
    
    /// <summary>
    /// KMS configuration
    /// </summary>
    public KmsConfiguration Kms { get; set; } = new();
    
    /// <summary>
    /// IAM configuration
    /// </summary>
    public IamConfiguration Iam { get; set; } = new();
}

/// <summary>
/// SQS-specific configuration
/// </summary>
public class SqsConfiguration
{
    /// <summary>
    /// Message retention period in seconds (default: 14 days)
    /// </summary>
    public int MessageRetentionPeriod { get; set; } = 1209600;
    
    /// <summary>
    /// Visibility timeout in seconds
    /// </summary>
    public int VisibilityTimeout { get; set; } = 30;
    
    /// <summary>
    /// Maximum receive count for dead letter queue
    /// </summary>
    public int MaxReceiveCount { get; set; } = 3;
    
    /// <summary>
    /// Whether to enable dead letter queue
    /// </summary>
    public bool EnableDeadLetterQueue { get; set; } = true;
    
    /// <summary>
    /// Default queue attributes
    /// </summary>
    public Dictionary<string, string> DefaultAttributes { get; set; } = new();
}

/// <summary>
/// SNS-specific configuration
/// </summary>
public class SnsConfiguration
{
    /// <summary>
    /// Default topic attributes
    /// </summary>
    public Dictionary<string, string> DefaultAttributes { get; set; } = new();
    
    /// <summary>
    /// Whether to enable message filtering
    /// </summary>
    public bool EnableMessageFiltering { get; set; } = true;
}

/// <summary>
/// KMS-specific configuration
/// </summary>
public class KmsConfiguration
{
    /// <summary>
    /// Default key alias for testing
    /// </summary>
    public string DefaultKeyAlias { get; set; } = "sourceflow-test";
    
    /// <summary>
    /// Key rotation enabled
    /// </summary>
    public bool EnableKeyRotation { get; set; } = false;
    
    /// <summary>
    /// Encryption algorithm to use
    /// </summary>
    public string EncryptionAlgorithm { get; set; } = "SYMMETRIC_DEFAULT";
}

/// <summary>
/// IAM-specific configuration
/// </summary>
public class IamConfiguration
{
    /// <summary>
    /// Whether to enforce IAM policies in LocalStack
    /// </summary>
    public bool EnforceIamPolicies { get; set; } = false;
    
    /// <summary>
    /// Load AWS managed policies in LocalStack
    /// </summary>
    public bool LoadManagedPolicies { get; set; } = false;
}

/// <summary>
/// Performance test configuration
/// </summary>
public class PerformanceTestConfiguration
{
    /// <summary>
    /// Default number of concurrent senders for throughput tests
    /// </summary>
    public int DefaultConcurrentSenders { get; set; } = 10;
    
    /// <summary>
    /// Default number of messages per sender
    /// </summary>
    public int DefaultMessagesPerSender { get; set; } = 100;
    
    /// <summary>
    /// Default message size in bytes
    /// </summary>
    public int DefaultMessageSize { get; set; } = 1024;
    
    /// <summary>
    /// Performance test timeout
    /// </summary>
    public TimeSpan TestTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Security test configuration
/// </summary>
public class SecurityTestConfiguration
{
    /// <summary>
    /// Whether to test encryption in transit
    /// </summary>
    public bool TestEncryptionInTransit { get; set; } = true;
    
    /// <summary>
    /// Whether to test IAM permissions
    /// </summary>
    public bool TestIamPermissions { get; set; } = true;
    
    /// <summary>
    /// Whether to test sensitive data masking
    /// </summary>
    public bool TestSensitiveDataMasking { get; set; } = true;
}