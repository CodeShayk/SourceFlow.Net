namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Test scenario for AWS service equivalence testing between LocalStack and real AWS
/// </summary>
public class AwsTestScenario
{
    /// <summary>
    /// Unique prefix for test resources to prevent conflicts
    /// </summary>
    public string TestPrefix { get; set; } = "";
    
    /// <summary>
    /// Unique test identifier for isolation
    /// </summary>
    public string TestId { get; set; } = "";
    
    /// <summary>
    /// Number of messages to send in the test
    /// </summary>
    public int MessageCount { get; set; } = 1;
    
    /// <summary>
    /// Size of each message in bytes
    /// </summary>
    public int MessageSize { get; set; } = 256;
    
    /// <summary>
    /// Whether to use KMS encryption for messages
    /// </summary>
    public bool UseEncryption { get; set; } = false;
    
    /// <summary>
    /// Whether to enable dead letter queue handling
    /// </summary>
    public bool EnableDeadLetterQueue { get; set; } = false;
    
    /// <summary>
    /// Test execution timeout in seconds
    /// </summary>
    public int TestTimeoutSeconds { get; set; } = 60;
    
    /// <summary>
    /// AWS region for testing
    /// </summary>
    public string Region { get; set; } = "us-east-1";
    
    /// <summary>
    /// Whether to test FIFO queue functionality
    /// </summary>
    public bool UseFifoQueue { get; set; } = false;
    
    /// <summary>
    /// Whether to test SNS fan-out messaging
    /// </summary>
    public bool TestFanOutMessaging { get; set; } = false;
    
    /// <summary>
    /// Number of SNS subscribers for fan-out testing
    /// </summary>
    public int SubscriberCount { get; set; } = 1;
    
    /// <summary>
    /// Whether to test batch operations
    /// </summary>
    public bool TestBatchOperations { get; set; } = false;
    
    /// <summary>
    /// Batch size for batch operations (max 10 for SQS)
    /// </summary>
    public int BatchSize { get; set; } = 1;
    
    /// <summary>
    /// Additional test metadata
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
    
    /// <summary>
    /// Generate a unique resource name for this test scenario
    /// </summary>
    public string GenerateResourceName(string resourceType)
    {
        return $"{TestPrefix}-{resourceType}-{TestId}".ToLowerInvariant();
    }
    
    /// <summary>
    /// Generate a unique queue name for SQS testing
    /// </summary>
    public string GenerateQueueName(bool isFifo = false)
    {
        var baseName = GenerateResourceName("queue");
        return (isFifo || UseFifoQueue) ? $"{baseName}.fifo" : baseName;
    }
    
    /// <summary>
    /// Generate a unique topic name for SNS testing
    /// </summary>
    public string GenerateTopicName()
    {
        return GenerateResourceName("topic");
    }
    
    /// <summary>
    /// Generate a unique KMS key alias
    /// </summary>
    public string GenerateKmsKeyAlias()
    {
        return $"alias/{GenerateResourceName("key")}";
    }
    
    /// <summary>
    /// Generate test message content of specified size
    /// </summary>
    public string GenerateTestMessage(int? customSize = null)
    {
        var size = customSize ?? MessageSize;
        var baseMessage = $"Test message for scenario {TestId}";
        
        if (size <= baseMessage.Length)
            return baseMessage[..size];
        
        var padding = new string('X', size - baseMessage.Length);
        return baseMessage + padding;
    }
    
    /// <summary>
    /// Validate the test scenario configuration
    /// </summary>
    public bool IsValid()
    {
        return !string.IsNullOrEmpty(TestPrefix) &&
               !string.IsNullOrEmpty(TestId) &&
               MessageCount > 0 &&
               MessageSize >= 100 && // Minimum reasonable message size
               MessageSize <= 262144 && // SQS message size limit (256KB)
               TestTimeoutSeconds > 0 &&
               !string.IsNullOrEmpty(Region) &&
               SubscriberCount > 0 &&
               BatchSize > 0 &&
               BatchSize <= 10; // SQS batch limit
    }
    
    /// <summary>
    /// Get estimated resource count for this scenario
    /// </summary>
    public int GetEstimatedResourceCount()
    {
        var resourceCount = 1; // Base queue or topic
        
        if (EnableDeadLetterQueue)
            resourceCount++; // DLQ
        
        if (TestFanOutMessaging)
            resourceCount += SubscriberCount; // SNS subscribers
        
        if (UseEncryption)
            resourceCount++; // KMS key
        
        return resourceCount;
    }
    
    /// <summary>
    /// Check if scenario requires KMS functionality
    /// </summary>
    public bool RequiresKms()
    {
        return UseEncryption;
    }
    
    /// <summary>
    /// Check if scenario requires SNS functionality
    /// </summary>
    public bool RequiresSns()
    {
        return TestFanOutMessaging;
    }
    
    /// <summary>
    /// Check if scenario requires SQS functionality
    /// </summary>
    public bool RequiresSqs()
    {
        return true; // All scenarios use SQS as base
    }
    
    /// <summary>
    /// Get test tags for resource tagging
    /// </summary>
    public Dictionary<string, string> GetResourceTags()
    {
        return new Dictionary<string, string>
        {
            ["TestPrefix"] = TestPrefix,
            ["TestId"] = TestId,
            ["MessageCount"] = MessageCount.ToString(),
            ["MessageSize"] = MessageSize.ToString(),
            ["UseEncryption"] = UseEncryption.ToString(),
            ["UseFifoQueue"] = UseFifoQueue.ToString(),
            ["CreatedBy"] = "SourceFlow.Tests",
            ["CreatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };
    }
    
    /// <summary>
    /// Create a copy of this scenario with modified parameters
    /// </summary>
    public AwsTestScenario WithModifications(Action<AwsTestScenario> modifications)
    {
        var copy = new AwsTestScenario
        {
            TestPrefix = TestPrefix,
            TestId = TestId,
            MessageCount = MessageCount,
            MessageSize = MessageSize,
            UseEncryption = UseEncryption,
            EnableDeadLetterQueue = EnableDeadLetterQueue,
            TestTimeoutSeconds = TestTimeoutSeconds,
            Region = Region,
            UseFifoQueue = UseFifoQueue,
            TestFanOutMessaging = TestFanOutMessaging,
            SubscriberCount = SubscriberCount,
            TestBatchOperations = TestBatchOperations,
            BatchSize = BatchSize,
            Metadata = new Dictionary<string, string>(Metadata)
        };
        
        modifications(copy);
        return copy;
    }
}