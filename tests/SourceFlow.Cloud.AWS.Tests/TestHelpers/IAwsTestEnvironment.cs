using Amazon.IdentityManagement;
using Amazon.KeyManagementService;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Enhanced AWS test environment interface with full AWS service support
/// Provides comprehensive AWS service clients and resource management capabilities
/// </summary>
public interface IAwsTestEnvironment : ICloudTestEnvironment
{
    /// <summary>
    /// SQS client for queue operations
    /// </summary>
    IAmazonSQS SqsClient { get; }
    
    /// <summary>
    /// SNS client for topic operations
    /// </summary>
    IAmazonSimpleNotificationService SnsClient { get; }
    
    /// <summary>
    /// KMS client for encryption operations
    /// </summary>
    IAmazonKeyManagementService KmsClient { get; }
    
    /// <summary>
    /// IAM client for identity and access management
    /// </summary>
    IAmazonIdentityManagementService IamClient { get; }
    
    /// <summary>
    /// Create a FIFO SQS queue with the specified name
    /// </summary>
    /// <param name="queueName">Name of the queue (will be suffixed with .fifo if not already)</param>
    /// <param name="attributes">Optional queue attributes</param>
    /// <returns>Queue URL</returns>
    Task<string> CreateFifoQueueAsync(string queueName, Dictionary<string, string>? attributes = null);
    
    /// <summary>
    /// Create a standard SQS queue with the specified name
    /// </summary>
    /// <param name="queueName">Name of the queue</param>
    /// <param name="attributes">Optional queue attributes</param>
    /// <returns>Queue URL</returns>
    Task<string> CreateStandardQueueAsync(string queueName, Dictionary<string, string>? attributes = null);
    
    /// <summary>
    /// Create an SNS topic with the specified name
    /// </summary>
    /// <param name="topicName">Name of the topic</param>
    /// <param name="attributes">Optional topic attributes</param>
    /// <returns>Topic ARN</returns>
    Task<string> CreateTopicAsync(string topicName, Dictionary<string, string>? attributes = null);
    
    /// <summary>
    /// Create a KMS key with the specified alias
    /// </summary>
    /// <param name="keyAlias">Alias for the key (without 'alias/' prefix)</param>
    /// <param name="description">Optional key description</param>
    /// <returns>Key ID</returns>
    Task<string> CreateKmsKeyAsync(string keyAlias, string? description = null);
    
    /// <summary>
    /// Validate IAM permissions for a specific action and resource
    /// </summary>
    /// <param name="action">AWS action (e.g., "sqs:SendMessage")</param>
    /// <param name="resource">AWS resource ARN</param>
    /// <returns>True if permission is granted, false otherwise</returns>
    Task<bool> ValidateIamPermissionsAsync(string action, string resource);
    
    /// <summary>
    /// Delete a queue by URL
    /// </summary>
    /// <param name="queueUrl">Queue URL to delete</param>
    Task DeleteQueueAsync(string queueUrl);
    
    /// <summary>
    /// Delete a topic by ARN
    /// </summary>
    /// <param name="topicArn">Topic ARN to delete</param>
    Task DeleteTopicAsync(string topicArn);
    
    /// <summary>
    /// Delete a KMS key by ID or alias
    /// </summary>
    /// <param name="keyId">Key ID or alias</param>
    /// <param name="pendingWindowInDays">Pending deletion window (7-30 days)</param>
    Task DeleteKmsKeyAsync(string keyId, int pendingWindowInDays = 7);
    
    /// <summary>
    /// Get health status for all AWS services
    /// </summary>
    /// <returns>Health check results for each service</returns>
    Task<Dictionary<string, AwsHealthCheckResult>> GetHealthStatusAsync();
}