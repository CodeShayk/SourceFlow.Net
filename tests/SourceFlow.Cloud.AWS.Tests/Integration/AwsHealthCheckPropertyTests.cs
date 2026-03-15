using Amazon.KeyManagementService.Model;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using FsCheck;
using FsCheck.Xunit;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Property-based tests for AWS health check accuracy
/// Validates that health checks correctly identify service availability and permission issues
/// **Feature: aws-cloud-integration-testing, Property 8: AWS Health Check Accuracy**
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class AwsHealthCheckPropertyTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdQueues = new();
    private readonly List<string> _createdTopics = new();
    private readonly List<string> _createdKeys = new();
    
    public AwsHealthCheckPropertyTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
    }
    
    /// <summary>
    /// Property 8: AWS Health Check Accuracy
    /// For any AWS service configuration (SQS, SNS, KMS), health checks should accurately 
    /// reflect the actual availability, accessibility, and permission status of the service, 
    /// returning true when services are operational and false when they are not.
    /// **Validates: Requirements 4.1, 4.2, 4.3, 4.4, 4.5**
    /// </summary>
    // FsCheck 2.x does not support async Task properties — method must be void
    [Property(MaxTest = 10, Arbitrary = new[] { typeof(AwsHealthCheckGenerators) })]
    public void Property_AwsHealthCheckAccuracy(AwsHealthCheckScenario scenario) =>
        Property_AwsHealthCheckAccuracyAsync(scenario).GetAwaiter().GetResult();

    private async Task Property_AwsHealthCheckAccuracyAsync(AwsHealthCheckScenario scenario)
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }

        // Arrange - Create resources based on scenario
        var resources = await CreateTestResourcesAsync(scenario);

        try
        {
            // Act - Perform health checks on all services
            var healthResults = await PerformHealthChecksAsync(resources, scenario);

            // Assert - Health checks accurately reflect service availability
            AssertHealthCheckAccuracy(healthResults, resources, scenario);

            // Assert - Health checks detect accessibility issues
            AssertAccessibilityDetection(healthResults, resources, scenario);

            // Assert - Health checks validate permissions correctly
            AssertPermissionValidation(healthResults, resources, scenario);

            // Assert - Health checks complete within acceptable latency
            AssertHealthCheckPerformance(healthResults, scenario);

            // Assert - Health checks are reliable under concurrent access
            if (scenario.TestConcurrency)
            {
                await AssertConcurrentHealthCheckReliability(resources, scenario);
            }
        }
        finally
        {
            // Clean up resources
            await CleanupResourcesAsync(resources);
        }
    }
    
    /// <summary>
    /// Create test resources based on the scenario
    /// </summary>
    private async Task<AwsHealthCheckResources> CreateTestResourcesAsync(AwsHealthCheckScenario scenario)
    {
        var resources = new AwsHealthCheckResources();
        
        // Create SQS resources if needed
        if (scenario.TestSqs)
        {
            if (scenario.CreateValidQueue)
            {
                var queueName = $"health-test-{Guid.NewGuid():N}";
                resources.QueueUrl = await CreateStandardQueueAsync(queueName);
                resources.QueueExists = true;
            }
            else
            {
                // Use non-existent queue URL
                resources.QueueUrl = $"http://localhost:4566/000000000000/non-existent-{Guid.NewGuid():N}";
                resources.QueueExists = false;
            }
        }
        
        // Create SNS resources if needed
        if (scenario.TestSns)
        {
            if (scenario.CreateValidTopic)
            {
                var topicName = $"health-test-{Guid.NewGuid():N}";
                resources.TopicArn = await CreateTopicAsync(topicName);
                resources.TopicExists = true;
            }
            else
            {
                // Use non-existent topic ARN
                resources.TopicArn = $"arn:aws:sns:us-east-1:000000000000:non-existent-{Guid.NewGuid():N}";
                resources.TopicExists = false;
            }
        }
        
        // Create KMS resources if needed
        if (scenario.TestKms)
        {
            if (scenario.CreateValidKey)
            {
                try
                {
                    var keyAlias = $"health-test-{Guid.NewGuid():N}";
                    resources.KeyId = await CreateKmsKeyAsync(keyAlias);
                    resources.KeyExists = true;
                }
                catch (Exception ex) when (ex.Message.Contains("not supported") || ex.Message.Contains("not implemented"))
                {
                    // KMS might not be fully supported in LocalStack free tier
                    resources.KmsNotSupported = true;
                }
            }
            else
            {
                // Use non-existent key ID
                resources.KeyId = Guid.NewGuid().ToString();
                resources.KeyExists = false;
            }
        }
        
        return resources;
    }
    
    /// <summary>
    /// Perform health checks on all configured services
    /// </summary>
    private async Task<AwsHealthCheckResults> PerformHealthChecksAsync(
        AwsHealthCheckResources resources, 
        AwsHealthCheckScenario scenario)
    {
        var results = new AwsHealthCheckResults();
        
        // SQS health checks
        if (scenario.TestSqs && !string.IsNullOrEmpty(resources.QueueUrl))
        {
            results.SqsResult = await PerformSqsHealthCheckAsync(resources.QueueUrl);
        }
        
        // SNS health checks
        if (scenario.TestSns && !string.IsNullOrEmpty(resources.TopicArn))
        {
            results.SnsResult = await PerformSnsHealthCheckAsync(resources.TopicArn);
        }
        
        // KMS health checks
        if (scenario.TestKms && !string.IsNullOrEmpty(resources.KeyId) && !resources.KmsNotSupported)
        {
            results.KmsResult = await PerformKmsHealthCheckAsync(resources.KeyId);
        }
        
        return results;
    }
    
    /// <summary>
    /// Perform SQS health check
    /// </summary>
    private async Task<ServiceHealthCheckResult> PerformSqsHealthCheckAsync(string queueUrl)
    {
        var result = new ServiceHealthCheckResult { ServiceName = "SQS" };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            // Check queue existence and accessibility
            var attributesResponse = await _localStack.SqsClient!.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                AttributeNames = new List<string> { "QueueArn", "ApproximateNumberOfMessages" }
            });
            
            result.IsAvailable = true;
            result.IsAccessible = attributesResponse.Attributes.ContainsKey("QueueArn");
            
            // Check send permission
            try
            {
                await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = queueUrl,
                    MessageBody = "Health check test"
                });
                result.HasSendPermission = true;
            }
            catch
            {
                result.HasSendPermission = false;
            }
            
            // Check receive permission
            try
            {
                await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 0
                });
                result.HasReceivePermission = true;
            }
            catch
            {
                result.HasReceivePermission = false;
            }
        }
        catch (Amazon.SQS.Model.QueueDoesNotExistException)
        {
            result.IsAvailable = false;
            result.IsAccessible = false;
            result.ErrorMessage = "Queue does not exist";
        }
        catch (Exception ex)
        {
            result.IsAvailable = false;
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            result.ResponseTime = stopwatch.Elapsed;
        }
        
        return result;
    }
    
    /// <summary>
    /// Perform SNS health check
    /// </summary>
    private async Task<ServiceHealthCheckResult> PerformSnsHealthCheckAsync(string topicArn)
    {
        var result = new ServiceHealthCheckResult { ServiceName = "SNS" };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            // Check topic existence and accessibility
            var attributesResponse = await _localStack.SnsClient!.GetTopicAttributesAsync(new GetTopicAttributesRequest
            {
                TopicArn = topicArn
            });
            
            result.IsAvailable = true;
            result.IsAccessible = attributesResponse.Attributes.ContainsKey("TopicArn");
            
            // Check publish permission
            try
            {
                await _localStack.SnsClient.PublishAsync(new PublishRequest
                {
                    TopicArn = topicArn,
                    Message = "Health check test"
                });
                result.HasPublishPermission = true;
            }
            catch
            {
                result.HasPublishPermission = false;
            }
            
            // Check subscription management permission
            try
            {
                await _localStack.SnsClient.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest
                {
                    TopicArn = topicArn
                });
                result.HasSubscriptionPermission = true;
            }
            catch
            {
                result.HasSubscriptionPermission = false;
            }
        }
        catch (Amazon.SimpleNotificationService.Model.NotFoundException)
        {
            result.IsAvailable = false;
            result.IsAccessible = false;
            result.ErrorMessage = "Topic does not exist";
        }
        catch (Exception ex)
        {
            result.IsAvailable = false;
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            result.ResponseTime = stopwatch.Elapsed;
        }
        
        return result;
    }
    
    /// <summary>
    /// Perform KMS health check
    /// </summary>
    private async Task<ServiceHealthCheckResult> PerformKmsHealthCheckAsync(string keyId)
    {
        var result = new ServiceHealthCheckResult { ServiceName = "KMS" };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            // Check key existence and accessibility
            var describeResponse = await _localStack.KmsClient!.DescribeKeyAsync(new DescribeKeyRequest
            {
                KeyId = keyId
            });
            
            result.IsAvailable = true;
            result.IsAccessible = describeResponse.KeyMetadata != null;
            result.KeyEnabled = describeResponse.KeyMetadata?.Enabled ?? false;
            
            // Check encryption permission
            try
            {
                var plaintext = System.Text.Encoding.UTF8.GetBytes("Health check test");
                await _localStack.KmsClient.EncryptAsync(new EncryptRequest
                {
                    KeyId = keyId,
                    Plaintext = new MemoryStream(plaintext)
                });
                result.HasEncryptPermission = true;
            }
            catch
            {
                result.HasEncryptPermission = false;
            }
        }
        catch (Amazon.KeyManagementService.Model.NotFoundException)
        {
            result.IsAvailable = false;
            result.IsAccessible = false;
            result.ErrorMessage = "Key does not exist";
        }
        catch (Exception ex)
        {
            result.IsAvailable = false;
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            result.ResponseTime = stopwatch.Elapsed;
        }
        
        return result;
    }
    
    /// <summary>
    /// Assert that health checks accurately reflect service availability
    /// </summary>
    private void AssertHealthCheckAccuracy(
        AwsHealthCheckResults results, 
        AwsHealthCheckResources resources, 
        AwsHealthCheckScenario scenario)
    {
        // SQS availability accuracy
        if (scenario.TestSqs && results.SqsResult != null)
        {
            if (resources.QueueExists)
            {
                Assert.True(results.SqsResult.IsAvailable, 
                    "Health check should report SQS queue as available when it exists");
            }
            else
            {
                Assert.False(results.SqsResult.IsAvailable, 
                    "Health check should report SQS queue as unavailable when it doesn't exist");
            }
        }
        
        // SNS availability accuracy
        if (scenario.TestSns && results.SnsResult != null)
        {
            if (resources.TopicExists)
            {
                Assert.True(results.SnsResult.IsAvailable, 
                    "Health check should report SNS topic as available when it exists");
            }
            else
            {
                Assert.False(results.SnsResult.IsAvailable, 
                    "Health check should report SNS topic as unavailable when it doesn't exist");
            }
        }
        
        // KMS availability accuracy
        if (scenario.TestKms && results.KmsResult != null && !resources.KmsNotSupported)
        {
            if (resources.KeyExists)
            {
                Assert.True(results.KmsResult.IsAvailable, 
                    "Health check should report KMS key as available when it exists");
            }
            else
            {
                Assert.False(results.KmsResult.IsAvailable, 
                    "Health check should report KMS key as unavailable when it doesn't exist");
            }
        }
    }
    
    /// <summary>
    /// Assert that health checks detect accessibility issues
    /// </summary>
    private void AssertAccessibilityDetection(
        AwsHealthCheckResults results, 
        AwsHealthCheckResources resources, 
        AwsHealthCheckScenario scenario)
    {
        // SQS accessibility
        if (scenario.TestSqs && results.SqsResult != null && resources.QueueExists)
        {
            Assert.True(results.SqsResult.IsAccessible, 
                "Health check should detect that existing SQS queue is accessible");
        }
        
        // SNS accessibility
        if (scenario.TestSns && results.SnsResult != null && resources.TopicExists)
        {
            Assert.True(results.SnsResult.IsAccessible, 
                "Health check should detect that existing SNS topic is accessible");
        }
        
        // KMS accessibility
        if (scenario.TestKms && results.KmsResult != null && resources.KeyExists && !resources.KmsNotSupported)
        {
            Assert.True(results.KmsResult.IsAccessible, 
                "Health check should detect that existing KMS key is accessible");
        }
    }
    
    /// <summary>
    /// Assert that health checks validate permissions correctly
    /// </summary>
    private void AssertPermissionValidation(
        AwsHealthCheckResults results, 
        AwsHealthCheckResources resources, 
        AwsHealthCheckScenario scenario)
    {
        // SQS permissions (in LocalStack, permissions are typically granted)
        if (scenario.TestSqs && results.SqsResult != null && resources.QueueExists)
        {
            Assert.True(results.SqsResult.HasSendPermission, 
                "Health check should detect send permission for existing SQS queue");
            Assert.True(results.SqsResult.HasReceivePermission, 
                "Health check should detect receive permission for existing SQS queue");
        }
        
        // SNS permissions
        if (scenario.TestSns && results.SnsResult != null && resources.TopicExists)
        {
            Assert.True(results.SnsResult.HasPublishPermission, 
                "Health check should detect publish permission for existing SNS topic");
            Assert.True(results.SnsResult.HasSubscriptionPermission, 
                "Health check should detect subscription permission for existing SNS topic");
        }
        
        // KMS permissions
        if (scenario.TestKms && results.KmsResult != null && resources.KeyExists && !resources.KmsNotSupported)
        {
            Assert.True(results.KmsResult.HasEncryptPermission, 
                "Health check should detect encryption permission for existing KMS key");
        }
    }
    
    /// <summary>
    /// Assert that health checks complete within acceptable latency
    /// </summary>
    private void AssertHealthCheckPerformance(
        AwsHealthCheckResults results, 
        AwsHealthCheckScenario scenario)
    {
        var maxAcceptableLatency = TimeSpan.FromSeconds(5);
        
        if (scenario.TestSqs && results.SqsResult != null)
        {
            Assert.True(results.SqsResult.ResponseTime < maxAcceptableLatency, 
                $"SQS health check should complete within {maxAcceptableLatency.TotalSeconds}s, took {results.SqsResult.ResponseTime.TotalSeconds}s");
        }
        
        if (scenario.TestSns && results.SnsResult != null)
        {
            Assert.True(results.SnsResult.ResponseTime < maxAcceptableLatency, 
                $"SNS health check should complete within {maxAcceptableLatency.TotalSeconds}s, took {results.SnsResult.ResponseTime.TotalSeconds}s");
        }
        
        if (scenario.TestKms && results.KmsResult != null)
        {
            Assert.True(results.KmsResult.ResponseTime < maxAcceptableLatency, 
                $"KMS health check should complete within {maxAcceptableLatency.TotalSeconds}s, took {results.KmsResult.ResponseTime.TotalSeconds}s");
        }
    }
    
    /// <summary>
    /// Assert that health checks are reliable under concurrent access
    /// </summary>
    private async Task AssertConcurrentHealthCheckReliability(
        AwsHealthCheckResources resources, 
        AwsHealthCheckScenario scenario)
    {
        var concurrentChecks = 10;
        var successCount = 0;
        var failureCount = 0;
        
        var tasks = Enumerable.Range(0, concurrentChecks).Select(async i =>
        {
            try
            {
                var results = await PerformHealthChecksAsync(resources, scenario);
                
                // Verify consistency of results
                if (scenario.TestSqs && results.SqsResult != null)
                {
                    if (results.SqsResult.IsAvailable == resources.QueueExists)
                    {
                        Interlocked.Increment(ref successCount);
                    }
                    else
                    {
                        Interlocked.Increment(ref failureCount);
                    }
                }
                
                if (scenario.TestSns && results.SnsResult != null)
                {
                    if (results.SnsResult.IsAvailable == resources.TopicExists)
                    {
                        Interlocked.Increment(ref successCount);
                    }
                    else
                    {
                        Interlocked.Increment(ref failureCount);
                    }
                }
            }
            catch
            {
                Interlocked.Increment(ref failureCount);
            }
        });
        
        await Task.WhenAll(tasks);
        
        // At least 90% of concurrent health checks should be consistent
        var totalChecks = successCount + failureCount;
        if (totalChecks > 0)
        {
            var successRate = (double)successCount / totalChecks;
            Assert.True(successRate >= 0.9, 
                $"Concurrent health checks should be at least 90% consistent, got {successRate:P}");
        }
    }
    
    /// <summary>
    /// Clean up test resources
    /// </summary>
    private async Task CleanupResourcesAsync(AwsHealthCheckResources resources)
    {
        if (!string.IsNullOrEmpty(resources.QueueUrl) && resources.QueueExists)
        {
            try
            {
                await _localStack.SqsClient!.DeleteQueueAsync(new DeleteQueueRequest 
                { 
                    QueueUrl = resources.QueueUrl 
                });
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        
        if (!string.IsNullOrEmpty(resources.TopicArn) && resources.TopicExists)
        {
            try
            {
                await _localStack.SnsClient!.DeleteTopicAsync(new DeleteTopicRequest 
                { 
                    TopicArn = resources.TopicArn 
                });
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        
        if (!string.IsNullOrEmpty(resources.KeyId) && resources.KeyExists && !resources.KmsNotSupported)
        {
            try
            {
                await _localStack.KmsClient!.ScheduleKeyDeletionAsync(new ScheduleKeyDeletionRequest
                {
                    KeyId = resources.KeyId,
                    PendingWindowInDays = 7
                });
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
    
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
                    // Ignore cleanup errors
                }
            }
        }
    }
}

#region Test Models and Generators

/// <summary>
/// Scenario for AWS health check property testing
/// </summary>
public class AwsHealthCheckScenario
{
    public bool TestSqs { get; set; }
    public bool TestSns { get; set; }
    public bool TestKms { get; set; }
    public bool CreateValidQueue { get; set; }
    public bool CreateValidTopic { get; set; }
    public bool CreateValidKey { get; set; }
    public bool TestConcurrency { get; set; }
}

/// <summary>
/// Resources created for health check testing
/// </summary>
public class AwsHealthCheckResources
{
    public string? QueueUrl { get; set; }
    public bool QueueExists { get; set; }
    
    public string? TopicArn { get; set; }
    public bool TopicExists { get; set; }
    
    public string? KeyId { get; set; }
    public bool KeyExists { get; set; }
    public bool KmsNotSupported { get; set; }
}

/// <summary>
/// Results from health check operations
/// </summary>
public class AwsHealthCheckResults
{
    public ServiceHealthCheckResult? SqsResult { get; set; }
    public ServiceHealthCheckResult? SnsResult { get; set; }
    public ServiceHealthCheckResult? KmsResult { get; set; }
}

/// <summary>
/// Individual service health check result
/// </summary>
public class ServiceHealthCheckResult
{
    public string ServiceName { get; set; } = "";
    public bool IsAvailable { get; set; }
    public bool IsAccessible { get; set; }
    public bool HasSendPermission { get; set; }
    public bool HasReceivePermission { get; set; }
    public bool HasPublishPermission { get; set; }
    public bool HasSubscriptionPermission { get; set; }
    public bool HasEncryptPermission { get; set; }
    public bool KeyEnabled { get; set; }
    public TimeSpan ResponseTime { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// FsCheck generators for AWS health check scenarios
/// </summary>
public static class AwsHealthCheckGenerators
{
    /// <summary>
    /// Generate valid AWS health check scenarios
    /// </summary>
    public static Arbitrary<AwsHealthCheckScenario> AwsHealthCheckScenario()
    {
        var generator = from testSqs in Arb.Generate<bool>()
                       from testSns in Arb.Generate<bool>()
                       from testKms in Arb.Generate<bool>()
                       from createValidQueue in Arb.Generate<bool>()
                       from createValidTopic in Arb.Generate<bool>()
                       from createValidKey in Arb.Generate<bool>()
                       from testConcurrency in Gen.Frequency(
                           Tuple.Create(8, Gen.Constant(false)),  // 80% no concurrency test
                           Tuple.Create(2, Gen.Constant(true)))   // 20% with concurrency test
                       where testSqs || testSns || testKms  // At least one service must be tested
                       select new AwsHealthCheckScenario
                       {
                           TestSqs = testSqs,
                           TestSns = testSns,
                           TestKms = testKms,
                           CreateValidQueue = testSqs && createValidQueue,
                           CreateValidTopic = testSns && createValidTopic,
                           CreateValidKey = testKms && createValidKey,
                           TestConcurrency = testConcurrency
                       };
        
        return Arb.From(generator);
    }
}

#endregion
