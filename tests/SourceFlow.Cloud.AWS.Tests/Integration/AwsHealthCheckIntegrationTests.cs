using Amazon.KeyManagementService.Model;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Comprehensive integration tests for AWS health check functionality
/// Tests SQS queue health, SNS topic health, KMS key health, service connectivity, and health check performance
/// **Validates: Requirements 4.1, 4.2, 4.3, 4.4, 4.5**
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class AwsHealthCheckIntegrationTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdQueues = new();
    private readonly List<string> _createdTopics = new();
    private readonly List<string> _createdKeys = new();
    
    public AwsHealthCheckIntegrationTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
    }
    
    #region SQS Health Checks (Requirement 4.1)
    
    [Fact]
    public async Task SqsHealthCheck_ShouldDetectQueueExistence()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-health-queue-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        // Act - Check if queue exists
        var listResponse = await _localStack.SqsClient.ListQueuesAsync(new ListQueuesRequest
        {
            QueueNamePrefix = queueName
        });
        
        // Assert
        Assert.NotEmpty(listResponse.QueueUrls);
        Assert.Contains(queueUrl, listResponse.QueueUrls);
    }
    
    [Fact]
    public async Task SqsHealthCheck_ShouldDetectQueueAccessibility()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-health-access-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        // Act - Try to get queue attributes (tests accessibility)
        var attributesResponse = await _localStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = new List<string> { "All" }
        });
        
        // Assert
        Assert.NotNull(attributesResponse);
        Assert.NotEmpty(attributesResponse.Attributes);
        Assert.True(attributesResponse.Attributes.ContainsKey("QueueArn"));
    }
    
    [Fact]
    public async Task SqsHealthCheck_ShouldValidateSendMessagePermissions()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-health-send-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        // Act - Try to send a test message (validates send permissions)
        var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Health check test message"
        });
        
        // Assert
        Assert.NotNull(sendResponse);
        Assert.NotNull(sendResponse.MessageId);
        Assert.NotEmpty(sendResponse.MessageId);
    }
    
    [Fact]
    public async Task SqsHealthCheck_ShouldValidateReceiveMessagePermissions()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-health-receive-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        // Send a test message first
        await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = "Health check test message"
        });
        
        // Act - Try to receive messages (validates receive permissions)
        var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 1
        });
        
        // Assert
        Assert.NotNull(receiveResponse);
        Assert.NotEmpty(receiveResponse.Messages);
    }
    
    [Fact]
    public async Task SqsHealthCheck_ShouldDetectNonExistentQueue()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var nonExistentQueueUrl = $"http://localhost:4566/000000000000/non-existent-queue-{Guid.NewGuid():N}";
        
        // Act & Assert - Should throw exception for non-existent queue
        await Assert.ThrowsAsync<Amazon.SQS.Model.QueueDoesNotExistException>(async () =>
        {
            await _localStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = nonExistentQueueUrl,
                AttributeNames = new List<string> { "QueueArn" }
            });
        });
    }
    
    #endregion
    
    #region SNS Health Checks (Requirement 4.2)
    
    [Fact]
    public async Task SnsHealthCheck_ShouldDetectTopicAvailability()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SnsClient == null)
        {
            return;
        }
        
        // Arrange
        var topicName = $"test-health-topic-{Guid.NewGuid():N}";
        var topicArn = await CreateTopicAsync(topicName);
        
        // Act - List topics to verify availability
        var listResponse = await _localStack.SnsClient.ListTopicsAsync(new ListTopicsRequest());
        
        // Assert
        Assert.NotNull(listResponse);
        Assert.NotEmpty(listResponse.Topics);
        Assert.Contains(listResponse.Topics, t => t.TopicArn == topicArn);
    }
    
    [Fact]
    public async Task SnsHealthCheck_ShouldValidateTopicAttributes()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SnsClient == null)
        {
            return;
        }
        
        // Arrange
        var topicName = $"test-health-attrs-{Guid.NewGuid():N}";
        var topicArn = await CreateTopicAsync(topicName);
        
        // Act - Get topic attributes
        var attributesResponse = await _localStack.SnsClient.GetTopicAttributesAsync(new GetTopicAttributesRequest
        {
            TopicArn = topicArn
        });
        
        // Assert
        Assert.NotNull(attributesResponse);
        Assert.NotEmpty(attributesResponse.Attributes);
        Assert.True(attributesResponse.Attributes.ContainsKey("TopicArn"));
    }
    
    [Fact]
    public async Task SnsHealthCheck_ShouldValidatePublishPermissions()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SnsClient == null)
        {
            return;
        }
        
        // Arrange
        var topicName = $"test-health-publish-{Guid.NewGuid():N}";
        var topicArn = await CreateTopicAsync(topicName);
        
        // Act - Try to publish a test message
        var publishResponse = await _localStack.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = "Health check test message",
            Subject = "Health Check"
        });
        
        // Assert
        Assert.NotNull(publishResponse);
        Assert.NotNull(publishResponse.MessageId);
        Assert.NotEmpty(publishResponse.MessageId);
    }
    
    [Fact]
    public async Task SnsHealthCheck_ShouldDetectSubscriptionStatus()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SnsClient == null || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var topicName = $"test-health-sub-{Guid.NewGuid():N}";
        var topicArn = await CreateTopicAsync(topicName);
        
        var queueName = $"test-health-sub-queue-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        var queueArn = await GetQueueArnAsync(queueUrl);
        
        // Subscribe queue to topic
        var subscribeResponse = await _localStack.SnsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn
        });
        
        // Act - List subscriptions for the topic
        var subscriptionsResponse = await _localStack.SnsClient.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest
        {
            TopicArn = topicArn
        });
        
        // Assert
        Assert.NotNull(subscriptionsResponse);
        Assert.NotEmpty(subscriptionsResponse.Subscriptions);
        Assert.Contains(subscriptionsResponse.Subscriptions, s => s.SubscriptionArn == subscribeResponse.SubscriptionArn);
    }
    
    [Fact]
    public async Task SnsHealthCheck_ShouldDetectNonExistentTopic()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SnsClient == null)
        {
            return;
        }
        
        // Arrange
        var nonExistentTopicArn = $"arn:aws:sns:us-east-1:000000000000:non-existent-topic-{Guid.NewGuid():N}";
        
        // Act & Assert - Should throw exception for non-existent topic
        await Assert.ThrowsAsync<Amazon.SimpleNotificationService.Model.NotFoundException>(async () =>
        {
            await _localStack.SnsClient.GetTopicAttributesAsync(new GetTopicAttributesRequest
            {
                TopicArn = nonExistentTopicArn
            });
        });
    }
    
    #endregion
    
    #region KMS Health Checks (Requirement 4.3)
    
    [Fact]
    public async Task KmsHealthCheck_ShouldDetectKeyAccessibility()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.KmsClient == null)
        {
            return;
        }
        
        // Arrange
        var keyAlias = $"test-health-key-{Guid.NewGuid():N}";
        string? keyId = null;
        
        try
        {
            keyId = await CreateKmsKeyAsync(keyAlias);
            
            // Act - Describe the key to verify accessibility
            var describeResponse = await _localStack.KmsClient.DescribeKeyAsync(new DescribeKeyRequest
            {
                KeyId = keyId
            });
            
            // Assert
            Assert.NotNull(describeResponse);
            Assert.NotNull(describeResponse.KeyMetadata);
            Assert.Equal(keyId, describeResponse.KeyMetadata.KeyId);
            Assert.True(describeResponse.KeyMetadata.Enabled);
        }
        catch (Exception ex) when (ex.Message.Contains("not supported") || ex.Message.Contains("not implemented"))
        {
            // KMS might not be fully supported in LocalStack free tier
            // Skip this test gracefully
            return;
        }
    }
    
    [Fact]
    public async Task KmsHealthCheck_ShouldValidateEncryptionPermissions()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.KmsClient == null)
        {
            return;
        }
        
        // Arrange
        var keyAlias = $"test-health-encrypt-{Guid.NewGuid():N}";
        string? keyId = null;
        
        try
        {
            keyId = await CreateKmsKeyAsync(keyAlias);
            
            var plaintext = System.Text.Encoding.UTF8.GetBytes("Health check test data");
            
            // Act - Try to encrypt data
            var encryptResponse = await _localStack.KmsClient.EncryptAsync(new EncryptRequest
            {
                KeyId = keyId,
                Plaintext = new MemoryStream(plaintext)
            });
            
            // Assert
            Assert.NotNull(encryptResponse);
            Assert.NotNull(encryptResponse.CiphertextBlob);
            Assert.True(encryptResponse.CiphertextBlob.Length > 0);
        }
        catch (Exception ex) when (ex.Message.Contains("not supported") || ex.Message.Contains("not implemented"))
        {
            // KMS might not be fully supported in LocalStack free tier
            // Skip this test gracefully
            return;
        }
    }
    
    [Fact]
    public async Task KmsHealthCheck_ShouldValidateDecryptionPermissions()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.KmsClient == null)
        {
            return;
        }
        
        // Arrange
        var keyAlias = $"test-health-decrypt-{Guid.NewGuid():N}";
        string? keyId = null;
        
        try
        {
            keyId = await CreateKmsKeyAsync(keyAlias);
            
            var plaintext = System.Text.Encoding.UTF8.GetBytes("Health check test data");
            
            // Encrypt first
            var encryptResponse = await _localStack.KmsClient.EncryptAsync(new EncryptRequest
            {
                KeyId = keyId,
                Plaintext = new MemoryStream(plaintext)
            });
            
            // Act - Try to decrypt data
            var decryptResponse = await _localStack.KmsClient.DecryptAsync(new DecryptRequest
            {
                CiphertextBlob = encryptResponse.CiphertextBlob
            });
            
            // Assert
            Assert.NotNull(decryptResponse);
            Assert.NotNull(decryptResponse.Plaintext);
            
            var decryptedData = new byte[decryptResponse.Plaintext.Length];
            decryptResponse.Plaintext.Read(decryptedData, 0, decryptedData.Length);
            Assert.Equal(plaintext, decryptedData);
        }
        catch (Exception ex) when (ex.Message.Contains("not supported") || ex.Message.Contains("not implemented"))
        {
            // KMS might not be fully supported in LocalStack free tier
            // Skip this test gracefully
            return;
        }
    }
    
    [Fact]
    public async Task KmsHealthCheck_ShouldDetectKeyStatus()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.KmsClient == null)
        {
            return;
        }
        
        // Arrange
        var keyAlias = $"test-health-status-{Guid.NewGuid():N}";
        string? keyId = null;
        
        try
        {
            keyId = await CreateKmsKeyAsync(keyAlias);
            
            // Act - Get key metadata to check status
            var describeResponse = await _localStack.KmsClient.DescribeKeyAsync(new DescribeKeyRequest
            {
                KeyId = keyId
            });
            
            // Assert
            Assert.NotNull(describeResponse.KeyMetadata);
            Assert.Equal(KeyState.Enabled, describeResponse.KeyMetadata.KeyState);
            Assert.True(describeResponse.KeyMetadata.Enabled);
        }
        catch (Exception ex) when (ex.Message.Contains("not supported") || ex.Message.Contains("not implemented"))
        {
            // KMS might not be fully supported in LocalStack free tier
            // Skip this test gracefully
            return;
        }
    }
    
    [Fact]
    public async Task KmsHealthCheck_ShouldDetectNonExistentKey()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.KmsClient == null)
        {
            return;
        }
        
        // Arrange
        var nonExistentKeyId = Guid.NewGuid().ToString();
        
        try
        {
            // Act & Assert - Should throw exception for non-existent key
            await Assert.ThrowsAsync<Amazon.KeyManagementService.Model.NotFoundException>(async () =>
            {
                await _localStack.KmsClient.DescribeKeyAsync(new DescribeKeyRequest
                {
                    KeyId = nonExistentKeyId
                });
            });
        }
        catch (Exception ex) when (ex.Message.Contains("not supported") || ex.Message.Contains("not implemented"))
        {
            // KMS might not be fully supported in LocalStack free tier
            // Skip this test gracefully
            return;
        }
    }
    
    #endregion
    
    #region Service Connectivity (Requirement 4.4)
    
    [Fact]
    public async Task ServiceConnectivity_ShouldValidateSqsEndpointAvailability()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Act - Simple list operation to test connectivity
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var listResponse = await _localStack.SqsClient.ListQueuesAsync(new ListQueuesRequest());
        stopwatch.Stop();
        
        // Assert
        Assert.NotNull(listResponse);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, "SQS endpoint should respond within 5 seconds");
    }
    
    [Fact]
    public async Task ServiceConnectivity_ShouldValidateSnsEndpointAvailability()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SnsClient == null)
        {
            return;
        }
        
        // Act - Simple list operation to test connectivity
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var listResponse = await _localStack.SnsClient.ListTopicsAsync(new ListTopicsRequest());
        stopwatch.Stop();
        
        // Assert
        Assert.NotNull(listResponse);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, "SNS endpoint should respond within 5 seconds");
    }
    
    [Fact]
    public async Task ServiceConnectivity_ShouldValidateKmsEndpointAvailability()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.KmsClient == null)
        {
            return;
        }
        
        try
        {
            // Act - Simple list operation to test connectivity
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var listResponse = await _localStack.KmsClient.ListKeysAsync(new ListKeysRequest());
            stopwatch.Stop();
            
            // Assert
            Assert.NotNull(listResponse);
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, "KMS endpoint should respond within 5 seconds");
        }
        catch (Exception ex) when (ex.Message.Contains("not supported") || ex.Message.Contains("not implemented"))
        {
            // KMS might not be fully supported in LocalStack free tier
            // Skip this test gracefully
            return;
        }
    }
    
    [Fact]
    public async Task ServiceConnectivity_ShouldHandleMultipleConcurrentRequests()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null || _localStack.SnsClient == null)
        {
            return;
        }
        
        // Act - Make concurrent requests to multiple services
        var tasks = new List<Task>
        {
            _localStack.SqsClient.ListQueuesAsync(new ListQueuesRequest()),
            _localStack.SnsClient.ListTopicsAsync(new ListTopicsRequest()),
            _localStack.SqsClient.ListQueuesAsync(new ListQueuesRequest()),
            _localStack.SnsClient.ListTopicsAsync(new ListTopicsRequest())
        };
        
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(tasks);
        stopwatch.Stop();
        
        // Assert - All requests should complete successfully
        Assert.True(stopwatch.ElapsedMilliseconds < 10000, "Concurrent requests should complete within 10 seconds");
    }
    
    #endregion
    
    #region Health Check Performance (Requirement 4.5)
    
    [Fact]
    public async Task HealthCheckPerformance_ShouldCompleteWithinAcceptableLatency()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null || _localStack.SnsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-health-perf-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        var topicName = $"test-health-perf-{Guid.NewGuid():N}";
        var topicArn = await CreateTopicAsync(topicName);
        
        // Act - Perform comprehensive health check
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        var sqsCheck = await _localStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = new List<string> { "QueueArn" }
        });
        
        var snsCheck = await _localStack.SnsClient.GetTopicAttributesAsync(new GetTopicAttributesRequest
        {
            TopicArn = topicArn
        });
        
        stopwatch.Stop();
        
        // Assert
        Assert.NotNull(sqsCheck);
        Assert.NotNull(snsCheck);
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, "Health checks should complete within 2 seconds");
    }
    
    [Fact]
    public async Task HealthCheckPerformance_ShouldBeReliableUnderLoad()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Arrange
        var queueName = $"test-health-load-{Guid.NewGuid():N}";
        var queueUrl = await CreateStandardQueueAsync(queueName);
        
        var successCount = 0;
        var failureCount = 0;
        var iterations = 20;
        
        // Act - Perform multiple health checks rapidly
        var tasks = Enumerable.Range(0, iterations).Select(async i =>
        {
            try
            {
                await _localStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
                {
                    QueueUrl = queueUrl,
                    AttributeNames = new List<string> { "QueueArn" }
                });
                Interlocked.Increment(ref successCount);
            }
            catch
            {
                Interlocked.Increment(ref failureCount);
            }
        });
        
        await Task.WhenAll(tasks);
        
        // Assert - At least 95% success rate
        var successRate = (double)successCount / iterations;
        Assert.True(successRate >= 0.95, $"Health check success rate should be at least 95%, got {successRate:P}");
    }
    
    [Fact]
    public async Task HealthCheckPerformance_ShouldMeasureResponseTimes()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null || _localStack.SnsClient == null)
        {
            return;
        }
        
        // Arrange
        var measurements = new List<(string Service, TimeSpan ResponseTime)>();
        
        // Act - Measure response times for each service
        var sqsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _localStack.SqsClient.ListQueuesAsync(new ListQueuesRequest());
        sqsStopwatch.Stop();
        measurements.Add(("SQS", sqsStopwatch.Elapsed));
        
        var snsStopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _localStack.SnsClient.ListTopicsAsync(new ListTopicsRequest());
        snsStopwatch.Stop();
        measurements.Add(("SNS", snsStopwatch.Elapsed));
        
        try
        {
            var kmsStopwatch = System.Diagnostics.Stopwatch.StartNew();
            await _localStack.KmsClient.ListKeysAsync(new ListKeysRequest());
            kmsStopwatch.Stop();
            measurements.Add(("KMS", kmsStopwatch.Elapsed));
        }
        catch (Exception ex) when (ex.Message.Contains("not supported") || ex.Message.Contains("not implemented"))
        {
            // KMS might not be fully supported in LocalStack free tier
        }
        
        // Assert - All services should respond within reasonable time
        foreach (var (service, responseTime) in measurements)
        {
            Assert.True(responseTime.TotalMilliseconds < 3000, 
                $"{service} health check should complete within 3 seconds, took {responseTime.TotalMilliseconds}ms");
        }
    }
    
    #endregion
    
    #region Helper Methods
    
    private async Task<string> CreateStandardQueueAsync(string queueName)
    {
        var response = await _localStack.SqsClient!.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName
        });
        
        _createdQueues.Add(response.QueueUrl);
        return response.QueueUrl;
    }
    
    private async Task<string> CreateTopicAsync(string topicName)
    {
        var response = await _localStack.SnsClient!.CreateTopicAsync(new CreateTopicRequest
        {
            Name = topicName
        });
        
        _createdTopics.Add(response.TopicArn);
        return response.TopicArn;
    }
    
    private async Task<string> CreateKmsKeyAsync(string keyAlias)
    {
        var createKeyResponse = await _localStack.KmsClient!.CreateKeyAsync(new CreateKeyRequest
        {
            Description = $"Test key for health checks - {keyAlias}",
            KeyUsage = KeyUsageType.ENCRYPT_DECRYPT
        });
        
        var keyId = createKeyResponse.KeyMetadata.KeyId;
        _createdKeys.Add(keyId);
        
        // Create alias
        var aliasName = keyAlias.StartsWith("alias/") ? keyAlias : $"alias/{keyAlias}";
        await _localStack.KmsClient.CreateAliasAsync(new CreateAliasRequest
        {
            AliasName = aliasName,
            TargetKeyId = keyId
        });
        
        return keyId;
    }
    
    private async Task<string> GetQueueArnAsync(string queueUrl)
    {
        var response = await _localStack.SqsClient!.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = new List<string> { "QueueArn" }
        });
        
        return response.Attributes["QueueArn"];
    }
    
    #endregion
    
    public async ValueTask DisposeAsync()
    {
        // Clean up created resources
        if (_localStack.SqsClient != null)
        {
            foreach (var queueUrl in _createdQueues)
            {
                try
                {
                    await _localStack.SqsClient.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl });
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
        
        if (_localStack.SnsClient != null)
        {
            foreach (var topicArn in _createdTopics)
            {
                try
                {
                    await _localStack.SnsClient.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn });
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
        
        if (_localStack.KmsClient != null)
        {
            foreach (var keyId in _createdKeys)
            {
                try
                {
                    await _localStack.KmsClient.ScheduleKeyDeletionAsync(new ScheduleKeyDeletionRequest
                    {
                        KeyId = keyId,
                        PendingWindowInDays = 7
                    });
                }
                catch
                {
                    // Ignore cleanup errors - KMS might not be fully supported
                }
            }
        }
    }
}
