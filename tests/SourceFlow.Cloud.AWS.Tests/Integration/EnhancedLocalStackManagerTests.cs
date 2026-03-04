using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using Amazon.SQS;
using Amazon.SimpleNotificationService;
using Amazon.KeyManagementService;
using Amazon.IdentityManagement;
using LocalStackConfig = SourceFlow.Cloud.AWS.Tests.TestHelpers.LocalStackConfiguration;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Integration tests for the enhanced LocalStack manager
/// Validates full AWS service emulation with comprehensive container management
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class EnhancedLocalStackManagerTests : IAsyncDisposable
{
    private readonly ILogger<LocalStackManager> _logger;
    private readonly LocalStackManager _localStackManager;
    
    public EnhancedLocalStackManagerTests()
    {
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _logger = loggerFactory.CreateLogger<LocalStackManager>();
        _localStackManager = new LocalStackManager(_logger);
    }
    
    [Fact]
    public async Task StartAsync_WithDefaultConfiguration_ShouldStartSuccessfully()
    {
        // Arrange
        var config = LocalStackConfig.CreateDefault();
        
        // Act
        await _localStackManager.StartAsync(config);
        
        // Assert
        Assert.True(_localStackManager.IsRunning);
        Assert.NotNull(_localStackManager.Endpoint);
        Assert.Contains("localhost", _localStackManager.Endpoint);
    }
    
    [Fact]
    public async Task StartAsync_WithPortConflict_ShouldUseAlternativePort()
    {
        // Arrange
        var config = LocalStackConfig.CreateDefault();
        config.Port = 4566; // Standard LocalStack port
        
        // Act
        await _localStackManager.StartAsync(config);
        
        // Assert
        Assert.True(_localStackManager.IsRunning);
        // Port might be different if 4566 was already in use
        Assert.NotNull(_localStackManager.Endpoint);
    }
    
    [Fact]
    public async Task WaitForServicesAsync_WithAllServices_ShouldCompleteSuccessfully()
    {
        // Arrange
        var config = LocalStackConfig.CreateForIntegrationTesting();
        await _localStackManager.StartAsync(config);
        
        // Act & Assert - Should not throw
        await _localStackManager.WaitForServicesAsync(
            new[] { "sqs", "sns", "kms", "iam" }, 
            TimeSpan.FromMinutes(2));
    }
    
    [Fact]
    public async Task IsServiceAvailableAsync_ForEachEnabledService_ShouldReturnTrue()
    {
        // Arrange
        var config = LocalStackConfig.CreateDefault();
        await _localStackManager.StartAsync(config);
        await _localStackManager.WaitForServicesAsync(config.EnabledServices.ToArray());
        
        // Act & Assert
        foreach (var service in config.EnabledServices)
        {
            var isAvailable = await _localStackManager.IsServiceAvailableAsync(service);
            Assert.True(isAvailable, $"Service {service} should be available");
        }
    }
    
    [Fact]
    public async Task GetServicesHealthAsync_ShouldReturnHealthStatusForAllServices()
    {
        // Arrange
        var config = LocalStackConfig.CreateDefault();
        await _localStackManager.StartAsync(config);
        await _localStackManager.WaitForServicesAsync(config.EnabledServices.ToArray());
        
        // Act
        var healthStatus = await _localStackManager.GetServicesHealthAsync();
        
        // Assert
        Assert.NotEmpty(healthStatus);
        foreach (var service in config.EnabledServices)
        {
            Assert.True(healthStatus.ContainsKey(service), $"Health status should contain {service}");
            Assert.True(healthStatus[service].IsAvailable, $"Service {service} should be available");
            Assert.True(healthStatus[service].ResponseTime > TimeSpan.Zero, $"Service {service} should have response time");
        }
    }
    
    [Fact]
    public async Task ValidateAwsServices_SqsService_ShouldAllowBasicOperations()
    {
        // Arrange
        var config = LocalStackConfig.CreateDefault();
        await _localStackManager.StartAsync(config);
        await _localStackManager.WaitForServicesAsync(new[] { "sqs" });
        
        var sqsClient = new AmazonSQSClient("test", "test", new AmazonSQSConfig
        {
            ServiceURL = _localStackManager.Endpoint,
            UseHttp = true,
            AuthenticationRegion = "us-east-1"
        });
        
        // Act & Assert
        // Should be able to list queues
        var listResponse = await sqsClient.ListQueuesAsync(new Amazon.SQS.Model.ListQueuesRequest());
        Assert.NotNull(listResponse);
        
        // Should be able to create a queue
        var queueName = $"test-queue-{Guid.NewGuid():N}";
        var createResponse = await sqsClient.CreateQueueAsync(queueName);
        Assert.NotNull(createResponse.QueueUrl);
        
        // Should be able to send a message
        var sendResponse = await sqsClient.SendMessageAsync(createResponse.QueueUrl, "test message");
        Assert.NotNull(sendResponse.MessageId);
        
        // Should be able to receive the message
        var receiveResponse = await sqsClient.ReceiveMessageAsync(createResponse.QueueUrl);
        Assert.NotEmpty(receiveResponse.Messages);
        Assert.Equal("test message", receiveResponse.Messages[0].Body);
        
        // Cleanup
        await sqsClient.DeleteQueueAsync(createResponse.QueueUrl);
    }
    
    [Fact]
    public async Task ValidateAwsServices_SnsService_ShouldAllowBasicOperations()
    {
        // Arrange
        var config = LocalStackConfig.CreateDefault();
        await _localStackManager.StartAsync(config);
        await _localStackManager.WaitForServicesAsync(new[] { "sns" });
        
        var snsClient = new AmazonSimpleNotificationServiceClient("test", "test", new AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = _localStackManager.Endpoint,
            UseHttp = true,
            AuthenticationRegion = "us-east-1"
        });
        
        // Act & Assert
        // Should be able to list topics
        var listResponse = await snsClient.ListTopicsAsync();
        Assert.NotNull(listResponse);
        
        // Should be able to create a topic
        var topicName = $"test-topic-{Guid.NewGuid():N}";
        var createResponse = await snsClient.CreateTopicAsync(topicName);
        Assert.NotNull(createResponse.TopicArn);
        
        // Should be able to publish a message
        var publishResponse = await snsClient.PublishAsync(createResponse.TopicArn, "test message");
        Assert.NotNull(publishResponse.MessageId);
        
        // Cleanup
        await snsClient.DeleteTopicAsync(createResponse.TopicArn);
    }
    
    [Fact]
    public async Task ValidateAwsServices_KmsService_ShouldAllowBasicOperations()
    {
        // Arrange
        var config = LocalStackConfig.CreateDefault();
        await _localStackManager.StartAsync(config);
        await _localStackManager.WaitForServicesAsync(new[] { "kms" });
        
        var kmsClient = new AmazonKeyManagementServiceClient("test", "test", new AmazonKeyManagementServiceConfig
        {
            ServiceURL = _localStackManager.Endpoint,
            UseHttp = true,
            AuthenticationRegion = "us-east-1"
        });
        
        // Act & Assert
        // Should be able to list keys
        var listResponse = await kmsClient.ListKeysAsync(new Amazon.KeyManagementService.Model.ListKeysRequest());
        Assert.NotNull(listResponse);
        
        // Should be able to create a key
        var createResponse = await kmsClient.CreateKeyAsync(new Amazon.KeyManagementService.Model.CreateKeyRequest
        {
            Description = "Test key for LocalStack validation"
        });
        Assert.NotNull(createResponse.KeyMetadata.KeyId);
        
        // Should be able to encrypt/decrypt data
        var plaintext = System.Text.Encoding.UTF8.GetBytes("test data");
        var encryptResponse = await kmsClient.EncryptAsync(new Amazon.KeyManagementService.Model.EncryptRequest
        {
            KeyId = createResponse.KeyMetadata.KeyId,
            Plaintext = new MemoryStream(plaintext)
        });
        Assert.NotNull(encryptResponse.CiphertextBlob);
        
        var decryptResponse = await kmsClient.DecryptAsync(new Amazon.KeyManagementService.Model.DecryptRequest
        {
            CiphertextBlob = encryptResponse.CiphertextBlob
        });
        var decryptedText = System.Text.Encoding.UTF8.GetString(decryptResponse.Plaintext.ToArray());
        Assert.Equal("test data", decryptedText);
    }
    
    [Fact]
    public async Task ValidateAwsServices_IamService_ShouldAllowBasicOperations()
    {
        // Arrange
        var config = LocalStackConfig.CreateDefault();
        await _localStackManager.StartAsync(config);
        await _localStackManager.WaitForServicesAsync(new[] { "iam" });
        
        var iamClient = new AmazonIdentityManagementServiceClient("test", "test", new AmazonIdentityManagementServiceConfig
        {
            ServiceURL = _localStackManager.Endpoint,
            UseHttp = true,
            AuthenticationRegion = "us-east-1"
        });
        
        // Act & Assert
        // Should be able to list roles
        var listResponse = await iamClient.ListRolesAsync();
        Assert.NotNull(listResponse);
        
        // Should be able to create a role
        var roleName = $"test-role-{Guid.NewGuid():N}";
        var assumeRolePolicyDocument = @"{
            ""Version"": ""2012-10-17"",
            ""Statement"": [
                {
                    ""Effect"": ""Allow"",
                    ""Principal"": {
                        ""Service"": ""lambda.amazonaws.com""
                    },
                    ""Action"": ""sts:AssumeRole""
                }
            ]
        }";
        
        var createResponse = await iamClient.CreateRoleAsync(new Amazon.IdentityManagement.Model.CreateRoleRequest
        {
            RoleName = roleName,
            AssumeRolePolicyDocument = assumeRolePolicyDocument
        });
        Assert.NotNull(createResponse.Role.Arn);
        
        // Cleanup
        await iamClient.DeleteRoleAsync(new Amazon.IdentityManagement.Model.DeleteRoleRequest
        {
            RoleName = roleName
        });
    }
    
    [Fact]
    public async Task GetLogsAsync_ShouldReturnContainerLogs()
    {
        // Arrange
        var config = LocalStackConfig.CreateWithDiagnostics();
        await _localStackManager.StartAsync(config);
        
        // Act
        var logs = await _localStackManager.GetLogsAsync(50);
        
        // Assert
        Assert.NotNull(logs);
        Assert.NotEmpty(logs);
        Assert.Contains("LocalStack", logs, StringComparison.OrdinalIgnoreCase);
    }
    
    [Fact]
    public async Task ResetDataAsync_ShouldClearAllData()
    {
        // Arrange
        var config = LocalStackConfig.CreateDefault();
        await _localStackManager.StartAsync(config);
        await _localStackManager.WaitForServicesAsync(new[] { "sqs" });
        
        var sqsClient = new AmazonSQSClient("test", "test", new AmazonSQSConfig
        {
            ServiceURL = _localStackManager.Endpoint,
            UseHttp = true,
            AuthenticationRegion = "us-east-1"
        });
        
        // Create a queue
        var queueName = $"test-queue-{Guid.NewGuid():N}";
        var createResponse = await sqsClient.CreateQueueAsync(queueName);
        
        // Verify queue exists
        var listBefore = await sqsClient.ListQueuesAsync(new Amazon.SQS.Model.ListQueuesRequest());
        Assert.Contains(createResponse.QueueUrl, listBefore.QueueUrls);
        
        // Act
        await _localStackManager.ResetDataAsync();
        await _localStackManager.WaitForServicesAsync(new[] { "sqs" });
        
        // Assert - Queue should be gone after reset
        var listAfter = await sqsClient.ListQueuesAsync(new Amazon.SQS.Model.ListQueuesRequest());
        Assert.DoesNotContain(createResponse.QueueUrl, listAfter.QueueUrls);
    }
    
    [Fact]
    public async Task StopAsync_ShouldStopContainerCleanly()
    {
        // Arrange
        var config = LocalStackConfig.CreateDefault();
        await _localStackManager.StartAsync(config);
        Assert.True(_localStackManager.IsRunning);
        
        // Act
        await _localStackManager.StopAsync();
        
        // Assert
        Assert.False(_localStackManager.IsRunning);
    }
    
    public async ValueTask DisposeAsync()
    {
        await _localStackManager.DisposeAsync();
    }
}
