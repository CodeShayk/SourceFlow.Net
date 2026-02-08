using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// AWS resource manager implementation
/// Provides automated provisioning, tracking, and cleanup of AWS resources for testing
/// </summary>
public class AwsResourceManager : IAwsResourceManager
{
    private readonly IAwsTestEnvironment _testEnvironment;
    private readonly ILogger<AwsResourceManager> _logger;
    private readonly List<AwsResourceSet> _trackedResources;
    private readonly object _lock = new();
    private bool _disposed;
    
    public AwsResourceManager(IAwsTestEnvironment testEnvironment, ILogger<AwsResourceManager> logger)
    {
        _testEnvironment = testEnvironment ?? throw new ArgumentNullException(nameof(testEnvironment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _trackedResources = new List<AwsResourceSet>();
    }
    
    /// <inheritdoc />
    public async Task<AwsResourceSet> CreateTestResourcesAsync(string testPrefix, AwsResourceTypes resourceTypes = AwsResourceTypes.All)
    {
        if (string.IsNullOrWhiteSpace(testPrefix))
            throw new ArgumentException("Test prefix cannot be null or empty", nameof(testPrefix));
        
        _logger.LogInformation("Creating AWS test resources with prefix: {TestPrefix}", testPrefix);
        
        var resourceSet = new AwsResourceSet
        {
            TestPrefix = testPrefix,
            Tags = new Dictionary<string, string>
            {
                ["TestPrefix"] = testPrefix,
                ["CreatedBy"] = "SourceFlow.Tests",
                ["Environment"] = "Test",
                ["CreatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            }
        };
        
        try
        {
            // Create SQS queues
            if (resourceTypes.HasFlag(AwsResourceTypes.SqsQueues))
            {
                await CreateSqsResourcesAsync(resourceSet);
            }
            
            // Create SNS topics
            if (resourceTypes.HasFlag(AwsResourceTypes.SnsTopics))
            {
                await CreateSnsResourcesAsync(resourceSet);
            }
            
            // Create KMS keys
            if (resourceTypes.HasFlag(AwsResourceTypes.KmsKeys))
            {
                await CreateKmsResourcesAsync(resourceSet);
            }
            
            // Create IAM roles (if supported)
            if (resourceTypes.HasFlag(AwsResourceTypes.IamRoles))
            {
                await CreateIamResourcesAsync(resourceSet);
            }
            
            // Track the resource set
            lock (_lock)
            {
                _trackedResources.Add(resourceSet);
            }
            
            _logger.LogInformation("Created AWS test resources: {QueueCount} queues, {TopicCount} topics, {KeyCount} keys, {RoleCount} roles",
                resourceSet.QueueUrls.Count, resourceSet.TopicArns.Count, resourceSet.KmsKeyIds.Count, resourceSet.IamRoleArns.Count);
            
            return resourceSet;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create test resources for prefix: {TestPrefix}", testPrefix);
            
            // Attempt cleanup of partially created resources
            try
            {
                await CleanupResourcesAsync(resourceSet, force: true);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to cleanup partially created resources");
            }
            
            throw;
        }
    }
    
    /// <inheritdoc />
    public async Task CleanupResourcesAsync(AwsResourceSet resources, bool force = false)
    {
        if (resources == null || resources.IsEmpty)
            return;
        
        _logger.LogInformation("Cleaning up AWS test resources for prefix: {TestPrefix}", resources.TestPrefix);
        
        var errors = new List<string>();
        
        // Cleanup CloudFormation stacks first (they may contain other resources)
        foreach (var stackArn in resources.CloudFormationStacks.ToList())
        {
            try
            {
                await DeleteCloudFormationStackAsync(stackArn);
                resources.CloudFormationStacks.Remove(stackArn);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to delete CloudFormation stack {stackArn}: {ex.Message}");
                if (!force) throw;
            }
        }
        
        // Cleanup SQS queues
        foreach (var queueUrl in resources.QueueUrls.ToList())
        {
            try
            {
                await _testEnvironment.DeleteQueueAsync(queueUrl);
                resources.QueueUrls.Remove(queueUrl);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to delete queue {queueUrl}: {ex.Message}");
                if (!force) throw;
            }
        }
        
        // Cleanup SNS topics
        foreach (var topicArn in resources.TopicArns.ToList())
        {
            try
            {
                await _testEnvironment.DeleteTopicAsync(topicArn);
                resources.TopicArns.Remove(topicArn);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to delete topic {topicArn}: {ex.Message}");
                if (!force) throw;
            }
        }
        
        // Cleanup KMS keys (schedule for deletion)
        foreach (var keyId in resources.KmsKeyIds.ToList())
        {
            try
            {
                await _testEnvironment.DeleteKmsKeyAsync(keyId, pendingWindowInDays: 7);
                resources.KmsKeyIds.Remove(keyId);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to delete KMS key {keyId}: {ex.Message}");
                if (!force) throw;
            }
        }
        
        // Remove from tracked resources
        lock (_lock)
        {
            _trackedResources.Remove(resources);
        }
        
        if (errors.Any())
        {
            _logger.LogWarning("Cleanup completed with errors: {Errors}", string.Join("; ", errors));
        }
        else
        {
            _logger.LogInformation("Successfully cleaned up all resources for prefix: {TestPrefix}", resources.TestPrefix);
        }
    }
    
    /// <inheritdoc />
    public async Task<bool> ResourceExistsAsync(string resourceArn)
    {
        if (string.IsNullOrWhiteSpace(resourceArn))
            return false;
        
        try
        {
            // Determine resource type from ARN and check existence
            if (resourceArn.Contains(":sqs:"))
            {
                // For SQS, we need to convert ARN to URL or use the URL directly
                var queueUrl = resourceArn.StartsWith("https://") ? resourceArn : ConvertSqsArnToUrl(resourceArn);
                var response = await _testEnvironment.SqsClient.GetQueueAttributesAsync(new Amazon.SQS.Model.GetQueueAttributesRequest
                {
                    QueueUrl = queueUrl,
                    AttributeNames = new List<string> { "QueueArn" }
                });
                return response != null;
            }
            else if (resourceArn.Contains(":sns:"))
            {
                var response = await _testEnvironment.SnsClient.GetTopicAttributesAsync(new Amazon.SimpleNotificationService.Model.GetTopicAttributesRequest
                {
                    TopicArn = resourceArn
                });
                return response != null;
            }
            else if (resourceArn.Contains(":kms:"))
            {
                var response = await _testEnvironment.KmsClient.DescribeKeyAsync(new Amazon.KeyManagementService.Model.DescribeKeyRequest
                {
                    KeyId = resourceArn
                });
                return response?.KeyMetadata != null;
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }
    
    /// <inheritdoc />
    public async Task<IEnumerable<string>> ListTestResourcesAsync(string testPrefix)
    {
        var resources = new List<string>();
        
        try
        {
            // List SQS queues
            var queueResponse = await _testEnvironment.SqsClient.ListQueuesAsync(new Amazon.SQS.Model.ListQueuesRequest
            {
                QueueNamePrefix = testPrefix
            });
            resources.AddRange(queueResponse.QueueUrls);
            
            // List SNS topics (no prefix filter available, need to filter manually)
            var topicResponse = await _testEnvironment.SnsClient.ListTopicsAsync(new Amazon.SimpleNotificationService.Model.ListTopicsRequest());
            var filteredTopics = topicResponse.Topics
                .Where(t => t.TopicArn.Contains(testPrefix))
                .Select(t => t.TopicArn);
            resources.AddRange(filteredTopics);
            
            // List KMS keys (no prefix filter, need to check aliases)
            try
            {
                var keyResponse = await _testEnvironment.KmsClient.ListAliasesAsync(new Amazon.KeyManagementService.Model.ListAliasesRequest());
                var filteredKeys = keyResponse.Aliases
                    .Where(a => a.AliasName.Contains(testPrefix))
                    .Select(a => a.TargetKeyId)
                    .Where(k => !string.IsNullOrEmpty(k));
                resources.AddRange(filteredKeys!);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Failed to list KMS keys: {Error}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list some test resources for prefix: {TestPrefix}", testPrefix);
        }
        
        return resources;
    }
    
    /// <inheritdoc />
    public async Task<int> CleanupOldResourcesAsync(TimeSpan maxAge, string? testPrefix = null)
    {
        var cutoffTime = DateTime.UtcNow - maxAge;
        var cleanedCount = 0;
        
        List<AwsResourceSet> resourcesToCleanup;
        lock (_lock)
        {
            resourcesToCleanup = _trackedResources
                .Where(r => r.CreatedAt < cutoffTime)
                .Where(r => testPrefix == null || r.TestPrefix.StartsWith(testPrefix))
                .ToList();
        }
        
        foreach (var resourceSet in resourcesToCleanup)
        {
            try
            {
                await CleanupResourcesAsync(resourceSet, force: true);
                cleanedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup old resource set: {TestPrefix}", resourceSet.TestPrefix);
            }
        }
        
        _logger.LogInformation("Cleaned up {Count} old resource sets older than {MaxAge}", cleanedCount, maxAge);
        return cleanedCount;
    }
    
    /// <inheritdoc />
    public async Task<decimal> EstimateCostAsync(AwsResourceSet resources, TimeSpan duration)
    {
        // This is a simplified cost estimation
        // In a real implementation, you would use AWS Pricing API or Cost Explorer
        
        decimal estimatedCost = 0;
        
        // SQS: $0.40 per million requests (very rough estimate)
        estimatedCost += resources.QueueUrls.Count * 0.01m;
        
        // SNS: $0.50 per million requests
        estimatedCost += resources.TopicArns.Count * 0.01m;
        
        // KMS: $1.00 per key per month
        var monthlyFraction = (decimal)duration.TotalDays / 30;
        estimatedCost += resources.KmsKeyIds.Count * 1.00m * monthlyFraction;
        
        await Task.CompletedTask; // Placeholder for async pricing API calls
        
        return estimatedCost;
    }
    
    /// <inheritdoc />
    public async Task TagResourceAsync(string resourceArn, Dictionary<string, string> tags)
    {
        // AWS resource tagging is service-specific
        // This is a simplified implementation
        
        try
        {
            if (resourceArn.Contains(":sqs:"))
            {
                var queueUrl = resourceArn.StartsWith("https://") ? resourceArn : ConvertSqsArnToUrl(resourceArn);
                await _testEnvironment.SqsClient.TagQueueAsync(new Amazon.SQS.Model.TagQueueRequest
                {
                    QueueUrl = queueUrl,
                    Tags = tags
                });
            }
            else if (resourceArn.Contains(":sns:"))
            {
                var tagList = tags.Select(kvp => new Amazon.SimpleNotificationService.Model.Tag
                {
                    Key = kvp.Key,
                    Value = kvp.Value
                }).ToList();
                
                await _testEnvironment.SnsClient.TagResourceAsync(new Amazon.SimpleNotificationService.Model.TagResourceRequest
                {
                    ResourceArn = resourceArn,
                    Tags = tagList
                });
            }
            // KMS and IAM tagging would be implemented similarly
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to tag resource {ResourceArn}", resourceArn);
        }
    }
    
    /// <inheritdoc />
    public async Task<string> CreateCloudFormationStackAsync(string stackName, string templateBody, Dictionary<string, string>? parameters = null)
    {
        if (_testEnvironment.IsLocalEmulator)
        {
            _logger.LogWarning("CloudFormation is not supported in LocalStack free tier");
            throw new NotSupportedException("CloudFormation is not supported in LocalStack free tier");
        }
        
        var cfClient = new AmazonCloudFormationClient();
        
        var request = new CreateStackRequest
        {
            StackName = stackName,
            TemplateBody = templateBody,
            Capabilities = new List<string> { "CAPABILITY_IAM" }
        };
        
        if (parameters != null)
        {
            request.Parameters = parameters.Select(kvp => new Parameter
            {
                ParameterKey = kvp.Key,
                ParameterValue = kvp.Value
            }).ToList();
        }
        
        var response = await cfClient.CreateStackAsync(request);
        return response.StackId;
    }
    
    /// <inheritdoc />
    public async Task DeleteCloudFormationStackAsync(string stackName)
    {
        if (_testEnvironment.IsLocalEmulator)
        {
            return; // CloudFormation not supported in LocalStack
        }
        
        var cfClient = new AmazonCloudFormationClient();
        await cfClient.DeleteStackAsync(new DeleteStackRequest
        {
            StackName = stackName
        });
    }
    
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _logger.LogInformation("Disposing AWS resource manager and cleaning up tracked resources");
        
        List<AwsResourceSet> resourcesToCleanup;
        lock (_lock)
        {
            resourcesToCleanup = _trackedResources.ToList();
        }
        
        foreach (var resourceSet in resourcesToCleanup)
        {
            try
            {
                await CleanupResourcesAsync(resourceSet, force: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup resource set during disposal: {TestPrefix}", resourceSet.TestPrefix);
            }
        }
        
        _disposed = true;
    }
    
    private async Task CreateSqsResourcesAsync(AwsResourceSet resourceSet)
    {
        var prefix = resourceSet.TestPrefix;
        
        // Create standard queue
        var standardQueueUrl = await _testEnvironment.CreateStandardQueueAsync($"{prefix}-standard-queue");
        resourceSet.QueueUrls.Add(standardQueueUrl);
        
        // Create FIFO queue
        var fifoQueueUrl = await _testEnvironment.CreateFifoQueueAsync($"{prefix}-fifo-queue");
        resourceSet.QueueUrls.Add(fifoQueueUrl);
        
        // Tag queues
        foreach (var queueUrl in new[] { standardQueueUrl, fifoQueueUrl })
        {
            await TagResourceAsync(queueUrl, resourceSet.Tags);
        }
    }
    
    private async Task CreateSnsResourcesAsync(AwsResourceSet resourceSet)
    {
        var prefix = resourceSet.TestPrefix;
        
        // Create topic
        var topicArn = await _testEnvironment.CreateTopicAsync($"{prefix}-topic");
        resourceSet.TopicArns.Add(topicArn);
        
        // Tag topic
        await TagResourceAsync(topicArn, resourceSet.Tags);
    }
    
    private async Task CreateKmsResourcesAsync(AwsResourceSet resourceSet)
    {
        try
        {
            var prefix = resourceSet.TestPrefix;
            
            // Create KMS key
            var keyId = await _testEnvironment.CreateKmsKeyAsync($"{prefix}-key", $"Test key for {prefix}");
            resourceSet.KmsKeyIds.Add(keyId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to create KMS resources (might not be supported in LocalStack): {Error}", ex.Message);
        }
    }
    
    private async Task CreateIamResourcesAsync(AwsResourceSet resourceSet)
    {
        try
        {
            // IAM role creation is complex and might not be needed for basic tests
            // This is a placeholder for future implementation
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to create IAM resources: {Error}", ex.Message);
        }
    }
    
    private string ConvertSqsArnToUrl(string arn)
    {
        // Convert SQS ARN to URL format
        // ARN format: arn:aws:sqs:region:account-id:queue-name
        // URL format: https://sqs.region.amazonaws.com/account-id/queue-name
        
        var parts = arn.Split(':');
        if (parts.Length >= 6)
        {
            var region = parts[3];
            var accountId = parts[4];
            var queueName = parts[5];
            
            if (_testEnvironment.IsLocalEmulator)
            {
                return $"{_testEnvironment.SqsClient.Config.ServiceURL}/{accountId}/{queueName}";
            }
            else
            {
                return $"https://sqs.{region}.amazonaws.com/{accountId}/{queueName}";
            }
        }
        
        return arn; // Return as-is if parsing fails
    }
}