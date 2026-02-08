namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Test scenario for CI/CD integration testing
/// </summary>
public class CiCdTestScenario
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
    /// Whether to use LocalStack emulator or real AWS services
    /// </summary>
    public bool UseLocalStack { get; set; } = true;
    
    /// <summary>
    /// Number of parallel tests to execute
    /// </summary>
    public int ParallelTestCount { get; set; } = 1;
    
    /// <summary>
    /// Number of AWS resources to create per test
    /// </summary>
    public int ResourceCount { get; set; } = 1;
    
    /// <summary>
    /// Whether automatic resource cleanup is enabled
    /// </summary>
    public bool CleanupEnabled { get; set; } = true;
    
    /// <summary>
    /// Test execution timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;
    
    /// <summary>
    /// Whether to enable comprehensive error reporting
    /// </summary>
    public bool EnableDetailedReporting { get; set; } = true;
    
    /// <summary>
    /// AWS region for testing
    /// </summary>
    public string Region { get; set; } = "us-east-1";
    
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
        return isFifo ? $"{baseName}.fifo" : baseName;
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
    /// Validate the test scenario configuration
    /// </summary>
    public bool IsValid()
    {
        return !string.IsNullOrEmpty(TestPrefix) &&
               !string.IsNullOrEmpty(TestId) &&
               ParallelTestCount > 0 &&
               ResourceCount > 0 &&
               TimeoutSeconds > 0 &&
               !string.IsNullOrEmpty(Region);
    }
    
    /// <summary>
    /// Get estimated resource count for this scenario
    /// </summary>
    public int GetEstimatedResourceCount()
    {
        return ParallelTestCount * ResourceCount;
    }
    
    /// <summary>
    /// Check if scenario requires real AWS services
    /// </summary>
    public bool RequiresRealAwsServices()
    {
        return !UseLocalStack;
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
            ["Environment"] = UseLocalStack ? "LocalStack" : "AWS",
            ["CreatedBy"] = "SourceFlow.Tests",
            ["CreatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };
    }
}