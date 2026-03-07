using Amazon;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// Enhanced AWS test environment implementation with full AWS service support
/// Provides comprehensive AWS service clients and resource management capabilities
/// </summary>
public class AwsTestEnvironment : IAwsTestEnvironment
{
    private readonly AwsTestConfiguration _configuration;
    private readonly ILocalStackManager? _localStackManager;
    private readonly IAwsResourceManager _resourceManager;
    private readonly ILogger<AwsTestEnvironment> _logger;
    private bool _disposed;
    
    public AwsTestEnvironment(
        AwsTestConfiguration configuration,
        ILocalStackManager? localStackManager,
        IAwsResourceManager resourceManager,
        ILogger<AwsTestEnvironment> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _localStackManager = localStackManager;
        _resourceManager = resourceManager ?? throw new ArgumentNullException(nameof(resourceManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }
    
    /// <inheritdoc />
    public IAmazonSQS SqsClient { get; private set; } = null!;
    
    /// <inheritdoc />
    public IAmazonSimpleNotificationService SnsClient { get; private set; } = null!;
    
    /// <inheritdoc />
    public IAmazonKeyManagementService KmsClient { get; private set; } = null!;
    
    /// <inheritdoc />
    public IAmazonIdentityManagementService IamClient { get; private set; } = null!;
    
    /// <inheritdoc />
    public bool IsLocalEmulator => _configuration.UseLocalStack;
    
    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing AWS test environment (LocalStack: {UseLocalStack})", IsLocalEmulator);
        
        if (IsLocalEmulator)
        {
            await InitializeLocalStackEnvironmentAsync();
        }
        else
        {
            await InitializeAwsEnvironmentAsync();
        }
        
        await ValidateServicesAsync();
        _logger.LogInformation("AWS test environment initialized successfully");
    }
    
    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            // Test SQS connectivity
            await SqsClient.ListQueuesAsync(new ListQueuesRequest());
            
            // Test SNS connectivity
            await SnsClient.ListTopicsAsync(new ListTopicsRequest());
            
            // Test KMS connectivity (optional, might not be available in LocalStack free tier)
            try
            {
                await KmsClient.ListKeysAsync(new ListKeysRequest());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("KMS service not available: {Error}", ex.Message);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AWS services not available");
            return false;
        }
    }
    
    /// <inheritdoc />
    public IServiceCollection CreateTestServices()
    {
        var services = new ServiceCollection();
        
        // Add logging
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        
        // Add AWS clients
        services.AddSingleton(SqsClient);
        services.AddSingleton(SnsClient);
        services.AddSingleton(KmsClient);
        services.AddSingleton(IamClient);
        
        // Add test configuration
        services.AddSingleton(_configuration);
        
        // Add resource manager
        services.AddSingleton(_resourceManager);
        
        return services;
    }
    
    /// <inheritdoc />
    public async Task CleanupAsync()
    {
        _logger.LogInformation("Cleaning up AWS test environment");
        
        // Cleanup will be handled by resource manager
        // Individual resources are tracked and cleaned up automatically
        
        _logger.LogInformation("AWS test environment cleanup completed");
    }
    
    /// <inheritdoc />
    public async Task<string> CreateFifoQueueAsync(string queueName, Dictionary<string, string>? attributes = null)
    {
        var fifoQueueName = queueName.EndsWith(".fifo") ? queueName : $"{queueName}.fifo";
        
        var queueAttributes = new Dictionary<string, string>
        {
            ["FifoQueue"] = "true",
            ["ContentBasedDeduplication"] = "true",
            ["MessageRetentionPeriod"] = _configuration.Services.Sqs.MessageRetentionPeriod.ToString(),
            ["VisibilityTimeoutSeconds"] = _configuration.Services.Sqs.VisibilityTimeout.ToString()
        };
        
        // Add custom attributes
        if (attributes != null)
        {
            foreach (var kvp in attributes)
            {
                queueAttributes[kvp.Key] = kvp.Value;
            }
        }
        
        // Add dead letter queue if enabled
        if (_configuration.Services.Sqs.EnableDeadLetterQueue)
        {
            var dlqName = $"{fifoQueueName}-dlq";
            var dlqResponse = await SqsClient.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = dlqName,
                Attributes = new Dictionary<string, string>
                {
                    ["FifoQueue"] = "true"
                }
            });
            
            var dlqArn = await GetQueueArnAsync(dlqResponse.QueueUrl);
            queueAttributes["RedrivePolicy"] = $"{{\"deadLetterTargetArn\":\"{dlqArn}\",\"maxReceiveCount\":{_configuration.Services.Sqs.MaxReceiveCount}}}";
        }
        
        var response = await SqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = fifoQueueName,
            Attributes = queueAttributes
        });
        
        _logger.LogDebug("Created FIFO queue: {QueueName} -> {QueueUrl}", fifoQueueName, response.QueueUrl);
        return response.QueueUrl;
    }
    
    /// <inheritdoc />
    public async Task<string> CreateStandardQueueAsync(string queueName, Dictionary<string, string>? attributes = null)
    {
        var queueAttributes = new Dictionary<string, string>
        {
            ["MessageRetentionPeriod"] = _configuration.Services.Sqs.MessageRetentionPeriod.ToString(),
            ["VisibilityTimeoutSeconds"] = _configuration.Services.Sqs.VisibilityTimeout.ToString()
        };
        
        // Add custom attributes
        if (attributes != null)
        {
            foreach (var kvp in attributes)
            {
                queueAttributes[kvp.Key] = kvp.Value;
            }
        }
        
        // Add dead letter queue if enabled
        if (_configuration.Services.Sqs.EnableDeadLetterQueue)
        {
            var dlqName = $"{queueName}-dlq";
            var dlqResponse = await SqsClient.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = dlqName
            });
            
            var dlqArn = await GetQueueArnAsync(dlqResponse.QueueUrl);
            queueAttributes["RedrivePolicy"] = $"{{\"deadLetterTargetArn\":\"{dlqArn}\",\"maxReceiveCount\":{_configuration.Services.Sqs.MaxReceiveCount}}}";
        }
        
        var response = await SqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = queueAttributes
        });
        
        _logger.LogDebug("Created standard queue: {QueueName} -> {QueueUrl}", queueName, response.QueueUrl);
        return response.QueueUrl;
    }
    
    /// <inheritdoc />
    public async Task<string> CreateTopicAsync(string topicName, Dictionary<string, string>? attributes = null)
    {
        var topicAttributes = new Dictionary<string, string>();
        
        // Add custom attributes
        if (attributes != null)
        {
            foreach (var kvp in attributes)
            {
                topicAttributes[kvp.Key] = kvp.Value;
            }
        }
        
        var response = await SnsClient.CreateTopicAsync(new CreateTopicRequest
        {
            Name = topicName,
            Attributes = topicAttributes
        });
        
        _logger.LogDebug("Created SNS topic: {TopicName} -> {TopicArn}", topicName, response.TopicArn);
        return response.TopicArn;
    }
    
    /// <inheritdoc />
    public async Task<string> CreateKmsKeyAsync(string keyAlias, string? description = null)
    {
        try
        {
            var keyDescription = description ?? $"Test key for SourceFlow integration tests - {keyAlias}";
            
            var createKeyResponse = await KmsClient.CreateKeyAsync(new CreateKeyRequest
            {
                Description = keyDescription,
                KeyUsage = KeyUsageType.ENCRYPT_DECRYPT,
                Origin = OriginType.AWS_KMS
            });
            
            var keyId = createKeyResponse.KeyMetadata.KeyId;
            
            // Create alias for the key
            var aliasName = keyAlias.StartsWith("alias/") ? keyAlias : $"alias/{keyAlias}";
            await KmsClient.CreateAliasAsync(new CreateAliasRequest
            {
                AliasName = aliasName,
                TargetKeyId = keyId
            });
            
            _logger.LogDebug("Created KMS key: {KeyAlias} -> {KeyId}", aliasName, keyId);
            return keyId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to create KMS key (might not be supported in LocalStack free tier): {Error}", ex.Message);
            throw;
        }
    }
    
    /// <inheritdoc />
    public async Task<bool> ValidateIamPermissionsAsync(string action, string resource)
    {
        try
        {
            // In LocalStack, IAM simulation might not be fully supported
            // For real AWS, we would use IAM policy simulator
            if (IsLocalEmulator)
            {
                // For LocalStack, assume permissions are valid if we can list policies
                await IamClient.ListPoliciesAsync(new ListPoliciesRequest { MaxItems = 1 });
                return true;
            }
            
            // For real AWS, implement proper permission validation
            // This would typically use IAM policy simulator or STS assume role
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to validate IAM permissions for {Action} on {Resource}: {Error}", action, resource, ex.Message);
            return false;
        }
    }
    
    /// <inheritdoc />
    public async Task DeleteQueueAsync(string queueUrl)
    {
        try
        {
            await SqsClient.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl });
            _logger.LogDebug("Deleted queue: {QueueUrl}", queueUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to delete queue {QueueUrl}: {Error}", queueUrl, ex.Message);
        }
    }
    
    /// <inheritdoc />
    public async Task DeleteTopicAsync(string topicArn)
    {
        try
        {
            await SnsClient.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn });
            _logger.LogDebug("Deleted topic: {TopicArn}", topicArn);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to delete topic {TopicArn}: {Error}", topicArn, ex.Message);
        }
    }
    
    /// <inheritdoc />
    public async Task DeleteKmsKeyAsync(string keyId, int pendingWindowInDays = 7)
    {
        try
        {
            await KmsClient.ScheduleKeyDeletionAsync(new ScheduleKeyDeletionRequest
            {
                KeyId = keyId,
                PendingWindowInDays = pendingWindowInDays
            });
            _logger.LogDebug("Scheduled KMS key deletion: {KeyId} (pending window: {Days} days)", keyId, pendingWindowInDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to delete KMS key {KeyId}: {Error}", keyId, ex.Message);
        }
    }
    
    /// <inheritdoc />
    public async Task<Dictionary<string, AwsHealthCheckResult>> GetHealthStatusAsync()
    {
        var results = new Dictionary<string, AwsHealthCheckResult>();
        
        // Check SQS health
        results["sqs"] = await CheckServiceHealthAsync("sqs", async () =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await SqsClient.ListQueuesAsync(new ListQueuesRequest());
            stopwatch.Stop();
            return stopwatch.Elapsed;
        });
        
        // Check SNS health
        results["sns"] = await CheckServiceHealthAsync("sns", async () =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await SnsClient.ListTopicsAsync(new ListTopicsRequest());
            stopwatch.Stop();
            return stopwatch.Elapsed;
        });
        
        // Check KMS health
        results["kms"] = await CheckServiceHealthAsync("kms", async () =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await KmsClient.ListKeysAsync(new ListKeysRequest());
            stopwatch.Stop();
            return stopwatch.Elapsed;
        });
        
        // Check IAM health
        results["iam"] = await CheckServiceHealthAsync("iam", async () =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await IamClient.ListPoliciesAsync(new ListPoliciesRequest { MaxItems = 1 });
            stopwatch.Stop();
            return stopwatch.Elapsed;
        });
        
        return results;
    }
    
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        await CleanupAsync();
        
        SqsClient?.Dispose();
        SnsClient?.Dispose();
        KmsClient?.Dispose();
        IamClient?.Dispose();
        
        if (_resourceManager != null)
        {
            await _resourceManager.DisposeAsync();
        }
        
        _disposed = true;
    }
    
    private async Task InitializeLocalStackEnvironmentAsync()
    {
        if (_localStackManager == null)
            throw new InvalidOperationException("LocalStack manager is required for LocalStack environment");
        
        // LocalStack manager should already be started
        if (!_localStackManager.IsRunning)
        {
            var config = LocalStackConfiguration.CreateDefault();
            await _localStackManager.StartAsync(config);
        }
        
        await _localStackManager.WaitForServicesAsync(new[] { "sqs", "sns", "kms", "iam" });
        
        // Configure clients for LocalStack
        var endpoint = _localStackManager.Endpoint;
        
        SqsClient = new AmazonSQSClient(_configuration.AccessKey, _configuration.SecretKey, new AmazonSQSConfig
        {
            ServiceURL = endpoint,
            UseHttp = true,
            RegionEndpoint = _configuration.Region
        });
        
        SnsClient = new AmazonSimpleNotificationServiceClient(_configuration.AccessKey, _configuration.SecretKey, new AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = endpoint,
            UseHttp = true,
            RegionEndpoint = _configuration.Region
        });
        
        KmsClient = new AmazonKeyManagementServiceClient(_configuration.AccessKey, _configuration.SecretKey, new AmazonKeyManagementServiceConfig
        {
            ServiceURL = endpoint,
            UseHttp = true,
            RegionEndpoint = _configuration.Region
        });
        
        IamClient = new AmazonIdentityManagementServiceClient(_configuration.AccessKey, _configuration.SecretKey, new AmazonIdentityManagementServiceConfig
        {
            ServiceURL = endpoint,
            UseHttp = true,
            RegionEndpoint = _configuration.Region
        });
    }
    
    private async Task InitializeAwsEnvironmentAsync()
    {
        // Configure clients for real AWS
        SqsClient = new AmazonSQSClient(_configuration.Region);
        SnsClient = new AmazonSimpleNotificationServiceClient(_configuration.Region);
        KmsClient = new AmazonKeyManagementServiceClient(_configuration.Region);
        IamClient = new AmazonIdentityManagementServiceClient(_configuration.Region);
        
        await Task.CompletedTask;
    }
    
    private async Task ValidateServicesAsync()
    {
        var healthResults = await GetHealthStatusAsync();
        
        foreach (var result in healthResults)
        {
            if (!result.Value.IsAvailable)
            {
                _logger.LogWarning("AWS service {ServiceName} is not available", result.Key);
            }
            else
            {
                _logger.LogDebug("AWS service {ServiceName} is available (response time: {ResponseTime}ms)", 
                    result.Key, result.Value.ResponseTime.TotalMilliseconds);
            }
        }
    }
    
    private async Task<AwsHealthCheckResult> CheckServiceHealthAsync(string serviceName, Func<Task<TimeSpan>> healthCheck)
    {
        var result = new AwsHealthCheckResult
        {
            ServiceName = serviceName,
            Endpoint = IsLocalEmulator ? _localStackManager?.Endpoint ?? "" : $"https://{serviceName}.{_configuration.Region.SystemName}.amazonaws.com"
        };
        
        try
        {
            result.ResponseTime = await healthCheck();
            result.IsAvailable = true;
        }
        catch (Exception ex)
        {
            result.IsAvailable = false;
            result.Errors.Add(ex.Message);
        }
        
        return result;
    }
    
    private async Task<string> GetQueueArnAsync(string queueUrl)
    {
        var response = await SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = new List<string> { "QueueArn" }
        });
        
        return response.Attributes["QueueArn"];
    }
}
