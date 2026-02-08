namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Interface for managing AWS test resources
/// Provides automated provisioning, tracking, and cleanup of AWS resources for testing
/// </summary>
public interface IAwsResourceManager : IAsyncDisposable
{
    /// <summary>
    /// Create a complete set of test resources with unique naming
    /// </summary>
    /// <param name="testPrefix">Unique prefix for all resources</param>
    /// <param name="resourceTypes">Types of resources to create</param>
    /// <returns>Resource set with all created resources</returns>
    Task<AwsResourceSet> CreateTestResourcesAsync(string testPrefix, AwsResourceTypes resourceTypes = AwsResourceTypes.All);
    
    /// <summary>
    /// Clean up all resources in the specified resource set
    /// </summary>
    /// <param name="resources">Resource set to clean up</param>
    /// <param name="force">Force cleanup even if resources are in use</param>
    Task CleanupResourcesAsync(AwsResourceSet resources, bool force = false);
    
    /// <summary>
    /// Check if a specific AWS resource exists
    /// </summary>
    /// <param name="resourceArn">AWS resource ARN or identifier</param>
    /// <returns>True if resource exists</returns>
    Task<bool> ResourceExistsAsync(string resourceArn);
    
    /// <summary>
    /// List all test resources with the specified prefix
    /// </summary>
    /// <param name="testPrefix">Test prefix to filter by</param>
    /// <returns>List of resource identifiers</returns>
    Task<IEnumerable<string>> ListTestResourcesAsync(string testPrefix);
    
    /// <summary>
    /// Clean up all test resources older than the specified age
    /// </summary>
    /// <param name="maxAge">Maximum age of resources to keep</param>
    /// <param name="testPrefix">Optional prefix filter</param>
    /// <returns>Number of resources cleaned up</returns>
    Task<int> CleanupOldResourcesAsync(TimeSpan maxAge, string? testPrefix = null);
    
    /// <summary>
    /// Get cost estimate for the specified resource set
    /// </summary>
    /// <param name="resources">Resource set to estimate</param>
    /// <param name="duration">Expected usage duration</param>
    /// <returns>Estimated cost in USD</returns>
    Task<decimal> EstimateCostAsync(AwsResourceSet resources, TimeSpan duration);
    
    /// <summary>
    /// Tag resources for tracking and cost allocation
    /// </summary>
    /// <param name="resourceArn">Resource to tag</param>
    /// <param name="tags">Tags to apply</param>
    Task TagResourceAsync(string resourceArn, Dictionary<string, string> tags);
    
    /// <summary>
    /// Create a CloudFormation stack for complex resource provisioning
    /// </summary>
    /// <param name="stackName">Name of the CloudFormation stack</param>
    /// <param name="templateBody">CloudFormation template</param>
    /// <param name="parameters">Stack parameters</param>
    /// <returns>Stack ARN</returns>
    Task<string> CreateCloudFormationStackAsync(string stackName, string templateBody, Dictionary<string, string>? parameters = null);
    
    /// <summary>
    /// Delete a CloudFormation stack and all its resources
    /// </summary>
    /// <param name="stackName">Name of the stack to delete</param>
    Task DeleteCloudFormationStackAsync(string stackName);
}

/// <summary>
/// AWS resource set containing all created test resources
/// </summary>
public class AwsResourceSet
{
    /// <summary>
    /// Unique test prefix for all resources
    /// </summary>
    public string TestPrefix { get; set; } = "";
    
    /// <summary>
    /// SQS queue URLs
    /// </summary>
    public List<string> QueueUrls { get; set; } = new();
    
    /// <summary>
    /// SNS topic ARNs
    /// </summary>
    public List<string> TopicArns { get; set; } = new();
    
    /// <summary>
    /// KMS key IDs
    /// </summary>
    public List<string> KmsKeyIds { get; set; } = new();
    
    /// <summary>
    /// IAM role ARNs
    /// </summary>
    public List<string> IamRoleArns { get; set; } = new();
    
    /// <summary>
    /// CloudFormation stack ARNs
    /// </summary>
    public List<string> CloudFormationStacks { get; set; } = new();
    
    /// <summary>
    /// When the resource set was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Resource tags for tracking and cost allocation
    /// </summary>
    public Dictionary<string, string> Tags { get; set; } = new();
    
    /// <summary>
    /// Additional metadata about the resources
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
    
    /// <summary>
    /// Get all resource identifiers in this set
    /// </summary>
    public IEnumerable<string> GetAllResourceIds()
    {
        return QueueUrls
            .Concat(TopicArns)
            .Concat(KmsKeyIds)
            .Concat(IamRoleArns)
            .Concat(CloudFormationStacks);
    }
    
    /// <summary>
    /// Check if the resource set is empty
    /// </summary>
    public bool IsEmpty => !GetAllResourceIds().Any();
}

/// <summary>
/// Types of AWS resources to create
/// </summary>
[Flags]
public enum AwsResourceTypes
{
    None = 0,
    SqsQueues = 1,
    SnsTopics = 2,
    KmsKeys = 4,
    IamRoles = 8,
    All = SqsQueues | SnsTopics | KmsKeys | IamRoles
}

/// <summary>
/// AWS health check result for a specific service
/// </summary>
public class AwsHealthCheckResult
{
    /// <summary>
    /// AWS service name
    /// </summary>
    public string ServiceName { get; set; } = "";
    
    /// <summary>
    /// Whether the service is available
    /// </summary>
    public bool IsAvailable { get; set; }
    
    /// <summary>
    /// Response time for the health check
    /// </summary>
    public TimeSpan ResponseTime { get; set; }
    
    /// <summary>
    /// Service endpoint URL
    /// </summary>
    public string Endpoint { get; set; } = "";
    
    /// <summary>
    /// Additional service metrics
    /// </summary>
    public Dictionary<string, object> ServiceMetrics { get; set; } = new();
    
    /// <summary>
    /// Any errors encountered during health check
    /// </summary>
    public List<string> Errors { get; set; } = new();
    
    /// <summary>
    /// Timestamp of the health check
    /// </summary>
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}