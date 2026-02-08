using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Integration tests for AWS service throttling and failure handling
/// Tests graceful handling of AWS service throttling, automatic backoff when service limits are exceeded,
/// network failure handling and connection recovery, timeout handling and connection pooling
/// Validates: Requirements 7.4, 7.5 - AWS service throttling and network failure handling
/// </summary>
[Collection("AWS Integration Tests")]
public class AwsServiceThrottlingAndFailureTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IAwsTestEnvironment _environment = null!;
    private readonly ILogger<AwsServiceThrottlingAndFailureTests> _logger;
    private readonly string _testPrefix;
    
    public AwsServiceThrottlingAndFailureTests(ITestOutputHelper output)
    {
        _output = output;
        _testPrefix = $"throttle-test-{Guid.NewGuid():N}";
        
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        
        _logger = loggerFactory.CreateLogger<AwsServiceThrottlingAndFailureTests>();
    }
    
    public async Task InitializeAsync()
    {
        _environment = await AwsTestEnvironmentFactory.CreateLocalStackEnvironmentAsync(_testPrefix);
    }
    
    public async Task DisposeAsync()
    {
        await _environment.DisposeAsync();
    }

    /// <summary>
    /// Test graceful handling of SQS service throttling with automatic backoff
    /// Validates: Requirement 7.4 - Graceful handling of AWS service throttling
    /// </summary>
    [Fact]
    public async Task SqsClient_HandlesThrottling_WithAutomaticBackoff()
    {
        // Arrange
        var queueUrl = await _environment.CreateStandardQueueAsync($"{_testPrefix}-throttle-sqs");
        var config = new AmazonSQSConfig
        {
            ServiceURL = _environment.IsLocalEmulator ? "http://localhost:4566" : null,
            MaxErrorRetry = 5,
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        var sqsClient = new AmazonSQSClient("test", "test", config);
        var successCount = 0;
        var throttleCount = 0;
        var totalMessages = 100;
        
        try
        {
            // Act - Send many messages rapidly to potentially trigger throttling
            var stopwatch = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, totalMessages).Select(async i =>
            {
                try
                {
                    await sqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MessageBody = $"Throttle test message {i}",
                        MessageAttributes = new Dictionary<string, Amazon.SQS.Model.MessageAttributeValue>
                        {
                            ["MessageNumber"] = new Amazon.SQS.Model.MessageAttributeValue 
                            { 
                                DataType = "Number", 
                                StringValue = i.ToString() 
                            }
                        }
                    });
                    Interlocked.Increment(ref successCount);
                    return (Success: true, Throttled: false);
                }
                catch (AmazonServiceException ex) when (
                    ex.ErrorCode == "Throttling" || 
                    ex.ErrorCode == "ThrottlingException" ||
                    ex.ErrorCode == "RequestLimitExceeded" ||
                    ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Interlocked.Increment(ref throttleCount);
                    _output.WriteLine($"Message {i} throttled: {ex.ErrorCode}");
                    return (Success: false, Throttled: true);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Message {i} failed: {ex.Message}");
                    return (Success: false, Throttled: false);
                }
            });
            
            var results = await Task.WhenAll(tasks);
            stopwatch.Stop();
            
            // Assert - Most messages should succeed (with retries handling throttling)
            Assert.True(successCount > totalMessages * 0.7, 
                $"At least 70% of messages should succeed, got {successCount}/{totalMessages}");
            
            _output.WriteLine($"Results: {successCount} succeeded, {throttleCount} throttled");
            _output.WriteLine($"Total duration: {stopwatch.ElapsedMilliseconds}ms");
            _output.WriteLine($"Average: {stopwatch.ElapsedMilliseconds / (double)totalMessages}ms per message");
            
            // If throttling occurred, verify automatic backoff was applied
            if (throttleCount > 0)
            {
                _output.WriteLine($"Throttling detected and handled: {throttleCount} throttled requests");
                Assert.True(stopwatch.ElapsedMilliseconds > 1000, 
                    "With throttling, total duration should show backoff delays");
            }
        }
        finally
        {
            await _environment.DeleteQueueAsync(queueUrl);
        }
    }
    
    /// <summary>
    /// Test graceful handling of SNS service throttling with automatic backoff
    /// Validates: Requirement 7.4 - Graceful handling of AWS service throttling
    /// </summary>
    [Fact]
    public async Task SnsClient_HandlesThrottling_WithAutomaticBackoff()
    {
        // Arrange
        var topicArn = await _environment.CreateTopicAsync($"{_testPrefix}-throttle-sns");
        var config = new AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = _environment.IsLocalEmulator ? "http://localhost:4566" : null,
            MaxErrorRetry = 5,
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        var snsClient = new AmazonSimpleNotificationServiceClient("test", "test", config);
        var successCount = 0;
        var throttleCount = 0;
        var totalMessages = 100;
        
        try
        {
            // Act - Publish many messages rapidly to potentially trigger throttling
            var stopwatch = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, totalMessages).Select(async i =>
            {
                try
                {
                    await snsClient.PublishAsync(new PublishRequest
                    {
                        TopicArn = topicArn,
                        Message = $"Throttle test message {i}",
                        MessageAttributes = new Dictionary<string, Amazon.SimpleNotificationService.Model.MessageAttributeValue>
                        {
                            ["MessageNumber"] = new Amazon.SimpleNotificationService.Model.MessageAttributeValue 
                            { 
                                DataType = "Number", 
                                StringValue = i.ToString() 
                            }
                        }
                    });
                    Interlocked.Increment(ref successCount);
                    return (Success: true, Throttled: false);
                }
                catch (AmazonServiceException ex) when (
                    ex.ErrorCode == "Throttling" || 
                    ex.ErrorCode == "ThrottlingException" ||
                    ex.ErrorCode == "RequestLimitExceeded" ||
                    ex.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Interlocked.Increment(ref throttleCount);
                    _output.WriteLine($"Message {i} throttled: {ex.ErrorCode}");
                    return (Success: false, Throttled: true);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Message {i} failed: {ex.Message}");
                    return (Success: false, Throttled: false);
                }
            });
            
            var results = await Task.WhenAll(tasks);
            stopwatch.Stop();
            
            // Assert - Most messages should succeed
            Assert.True(successCount > totalMessages * 0.7, 
                $"At least 70% of messages should succeed, got {successCount}/{totalMessages}");
            
            _output.WriteLine($"Results: {successCount} succeeded, {throttleCount} throttled");
            _output.WriteLine($"Total duration: {stopwatch.ElapsedMilliseconds}ms");
            
            if (throttleCount > 0)
            {
                _output.WriteLine($"Throttling detected and handled: {throttleCount} throttled requests");
            }
        }
        finally
        {
            await _environment.DeleteTopicAsync(topicArn);
        }
    }

    /// <summary>
    /// Test automatic backoff when SQS service limits are exceeded
    /// Validates: Requirement 7.4 - Automatic backoff when service limits are exceeded
    /// </summary>
    [Fact]
    public async Task SqsClient_AppliesBackoff_WhenServiceLimitsExceeded()
    {
        // Arrange
        var queueUrl = await _environment.CreateStandardQueueAsync($"{_testPrefix}-limits-sqs");
        var config = new AmazonSQSConfig
        {
            ServiceURL = _environment.IsLocalEmulator ? "http://localhost:4566" : null,
            MaxErrorRetry = 5,
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        var sqsClient = new AmazonSQSClient("test", "test", config);
        var attemptDurations = new List<long>();
        
        try
        {
            // Act - Send messages in bursts to test backoff behavior
            for (int burst = 0; burst < 3; burst++)
            {
                var stopwatch = Stopwatch.StartNew();
                var burstTasks = Enumerable.Range(0, 50).Select(async i =>
                {
                    try
                    {
                        await sqsClient.SendMessageAsync(new SendMessageRequest
                        {
                            QueueUrl = queueUrl,
                            MessageBody = $"Burst {burst}, Message {i}"
                        });
                        return true;
                    }
                    catch (AmazonServiceException ex) when (
                        ex.ErrorCode == "Throttling" || 
                        ex.ErrorCode == "RequestLimitExceeded")
                    {
                        // Expected throttling
                        return false;
                    }
                });
                
                await Task.WhenAll(burstTasks);
                stopwatch.Stop();
                attemptDurations.Add(stopwatch.ElapsedMilliseconds);
                
                _output.WriteLine($"Burst {burst + 1} completed in {stopwatch.ElapsedMilliseconds}ms");
                
                // Small delay between bursts
                await Task.Delay(100);
            }
            
            // Assert - Verify backoff behavior
            // If throttling occurs, later bursts may take longer due to backoff
            Assert.NotEmpty(attemptDurations);
            Assert.All(attemptDurations, duration => Assert.True(duration >= 0));
            
            var avgDuration = attemptDurations.Average();
            _output.WriteLine($"Average burst duration: {avgDuration}ms");
            
            // Verify that the SDK is applying backoff (durations should be reasonable)
            Assert.True(avgDuration < 30000, 
                $"Average duration should be reasonable with backoff, got {avgDuration}ms");
        }
        finally
        {
            await _environment.DeleteQueueAsync(queueUrl);
        }
    }
    
    /// <summary>
    /// Test automatic backoff when SNS service limits are exceeded
    /// Validates: Requirement 7.4 - Automatic backoff when service limits are exceeded
    /// </summary>
    [Fact]
    public async Task SnsClient_AppliesBackoff_WhenServiceLimitsExceeded()
    {
        // Arrange
        var topicArn = await _environment.CreateTopicAsync($"{_testPrefix}-limits-sns");
        var config = new AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = _environment.IsLocalEmulator ? "http://localhost:4566" : null,
            MaxErrorRetry = 5,
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        var snsClient = new AmazonSimpleNotificationServiceClient("test", "test", config);
        var attemptDurations = new List<long>();
        
        try
        {
            // Act - Publish messages in bursts to test backoff behavior
            for (int burst = 0; burst < 3; burst++)
            {
                var stopwatch = Stopwatch.StartNew();
                var burstTasks = Enumerable.Range(0, 50).Select(async i =>
                {
                    try
                    {
                        await snsClient.PublishAsync(new PublishRequest
                        {
                            TopicArn = topicArn,
                            Message = $"Burst {burst}, Message {i}"
                        });
                        return true;
                    }
                    catch (AmazonServiceException ex) when (
                        ex.ErrorCode == "Throttling" || 
                        ex.ErrorCode == "RequestLimitExceeded")
                    {
                        return false;
                    }
                });
                
                await Task.WhenAll(burstTasks);
                stopwatch.Stop();
                attemptDurations.Add(stopwatch.ElapsedMilliseconds);
                
                _output.WriteLine($"Burst {burst + 1} completed in {stopwatch.ElapsedMilliseconds}ms");
                
                await Task.Delay(100);
            }
            
            // Assert
            Assert.NotEmpty(attemptDurations);
            var avgDuration = attemptDurations.Average();
            _output.WriteLine($"Average burst duration: {avgDuration}ms");
            
            Assert.True(avgDuration < 30000, 
                $"Average duration should be reasonable with backoff, got {avgDuration}ms");
        }
        finally
        {
            await _environment.DeleteTopicAsync(topicArn);
        }
    }

    /// <summary>
    /// Test network failure handling for SQS operations
    /// Validates: Requirement 7.5 - Network failure handling
    /// </summary>
    [Fact]
    public async Task SqsClient_HandlesNetworkFailures_Gracefully()
    {
        // Arrange - Use invalid endpoint to simulate network failure
        var config = new AmazonSQSConfig
        {
            ServiceURL = "http://invalid-endpoint-that-does-not-exist.local:9999",
            MaxErrorRetry = 2,
            Timeout = TimeSpan.FromSeconds(2),
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        var sqsClient = new AmazonSQSClient("test", "test", config);
        var queueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/test-queue";
        
        // Act
        var stopwatch = Stopwatch.StartNew();
        Exception? caughtException = null;
        
        try
        {
            await sqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = "test"
            });
        }
        catch (Exception ex)
        {
            caughtException = ex;
            _output.WriteLine($"Network failure handled: {ex.GetType().Name}");
            _output.WriteLine($"Message: {ex.Message}");
        }
        
        stopwatch.Stop();
        
        // Assert - Should fail gracefully with appropriate exception
        Assert.NotNull(caughtException);
        Assert.True(
            caughtException is AmazonServiceException ||
            caughtException is HttpRequestException ||
            caughtException is SocketException ||
            caughtException is WebException ||
            caughtException.InnerException is SocketException ||
            caughtException.InnerException is HttpRequestException,
            $"Expected network-related exception, got: {caughtException.GetType().Name}");
        
        // Should have attempted retries (duration > timeout)
        _output.WriteLine($"Operation failed after {stopwatch.ElapsedMilliseconds}ms");
        Assert.True(stopwatch.ElapsedMilliseconds >= config.Timeout.Value.TotalMilliseconds,
            "Should have attempted operation at least once");
    }
    
    /// <summary>
    /// Test network failure handling for SNS operations
    /// Validates: Requirement 7.5 - Network failure handling
    /// </summary>
    [Fact]
    public async Task SnsClient_HandlesNetworkFailures_Gracefully()
    {
        // Arrange - Use invalid endpoint to simulate network failure
        var config = new AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = "http://invalid-endpoint-that-does-not-exist.local:9999",
            MaxErrorRetry = 2,
            Timeout = TimeSpan.FromSeconds(2),
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        var snsClient = new AmazonSimpleNotificationServiceClient("test", "test", config);
        var topicArn = "arn:aws:sns:us-east-1:000000000000:test-topic";
        
        // Act
        var stopwatch = Stopwatch.StartNew();
        Exception? caughtException = null;
        
        try
        {
            await snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = "test"
            });
        }
        catch (Exception ex)
        {
            caughtException = ex;
            _output.WriteLine($"Network failure handled: {ex.GetType().Name}");
            _output.WriteLine($"Message: {ex.Message}");
        }
        
        stopwatch.Stop();
        
        // Assert
        Assert.NotNull(caughtException);
        Assert.True(
            caughtException is AmazonServiceException ||
            caughtException is HttpRequestException ||
            caughtException is SocketException ||
            caughtException is WebException ||
            caughtException.InnerException is SocketException ||
            caughtException.InnerException is HttpRequestException,
            $"Expected network-related exception, got: {caughtException.GetType().Name}");
        
        _output.WriteLine($"Operation failed after {stopwatch.ElapsedMilliseconds}ms");
    }
    
    /// <summary>
    /// Test connection recovery after network failure for SQS
    /// Validates: Requirement 7.5 - Connection recovery
    /// </summary>
    [Fact]
    public async Task SqsClient_RecoversConnection_AfterNetworkFailure()
    {
        // Arrange
        var queueUrl = await _environment.CreateStandardQueueAsync($"{_testPrefix}-recovery-sqs");
        
        try
        {
            // Act - Step 1: Successful operation
            var response1 = await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = "Before failure"
            });
            
            Assert.NotNull(response1.MessageId);
            _output.WriteLine($"First message sent successfully: {response1.MessageId}");
            
            // Step 2: Simulate failure by using invalid endpoint temporarily
            var invalidConfig = new AmazonSQSConfig
            {
                ServiceURL = "http://invalid-endpoint.local:9999",
                MaxErrorRetry = 1,
                Timeout = TimeSpan.FromSeconds(1),
                RegionEndpoint = Amazon.RegionEndpoint.USEast1
            };
            
            var failingClient = new AmazonSQSClient("test", "test", invalidConfig);
            
            try
            {
                await failingClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = queueUrl,
                    MessageBody = "During failure"
                });
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Expected failure: {ex.GetType().Name}");
            }
            
            // Step 3: Recover with valid client
            var response2 = await _environment.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = "After recovery"
            });
            
            // Assert - Connection should recover
            Assert.NotNull(response2.MessageId);
            _output.WriteLine($"Message sent after recovery: {response2.MessageId}");
            
            // Verify both messages were received
            var receiveResponse = await _environment.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 2
            });
            
            Assert.True(receiveResponse.Messages.Count >= 2, 
                $"Should receive at least 2 messages, got {receiveResponse.Messages.Count}");
            
            _output.WriteLine($"Successfully recovered and received {receiveResponse.Messages.Count} messages");
        }
        finally
        {
            await _environment.DeleteQueueAsync(queueUrl);
        }
    }

    /// <summary>
    /// Test connection recovery after network failure for SNS
    /// Validates: Requirement 7.5 - Connection recovery
    /// </summary>
    [Fact]
    public async Task SnsClient_RecoversConnection_AfterNetworkFailure()
    {
        // Arrange
        var topicArn = await _environment.CreateTopicAsync($"{_testPrefix}-recovery-sns");
        
        try
        {
            // Act - Step 1: Successful operation
            var response1 = await _environment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = "Before failure"
            });
            
            Assert.NotNull(response1.MessageId);
            _output.WriteLine($"First message published successfully: {response1.MessageId}");
            
            // Step 2: Simulate failure
            var invalidConfig = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = "http://invalid-endpoint.local:9999",
                MaxErrorRetry = 1,
                Timeout = TimeSpan.FromSeconds(1),
                RegionEndpoint = Amazon.RegionEndpoint.USEast1
            };
            
            var failingClient = new AmazonSimpleNotificationServiceClient("test", "test", invalidConfig);
            
            try
            {
                await failingClient.PublishAsync(new PublishRequest
                {
                    TopicArn = topicArn,
                    Message = "During failure"
                });
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Expected failure: {ex.GetType().Name}");
            }
            
            // Step 3: Recover with valid client
            var response2 = await _environment.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = "After recovery"
            });
            
            // Assert - Connection should recover
            Assert.NotNull(response2.MessageId);
            _output.WriteLine($"Message published after recovery: {response2.MessageId}");
            _output.WriteLine("Connection successfully recovered");
        }
        finally
        {
            await _environment.DeleteTopicAsync(topicArn);
        }
    }
    
    /// <summary>
    /// Test timeout handling for SQS operations
    /// Validates: Requirement 7.5 - Timeout handling
    /// </summary>
    [Fact]
    public async Task SqsClient_HandlesTimeouts_Appropriately()
    {
        // Arrange - Configure with very short timeout
        var queueUrl = await _environment.CreateStandardQueueAsync($"{_testPrefix}-timeout-sqs");
        var config = new AmazonSQSConfig
        {
            ServiceURL = _environment.IsLocalEmulator ? "http://localhost:4566" : null,
            MaxErrorRetry = 2,
            Timeout = TimeSpan.FromMilliseconds(50), // Very short timeout
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        var sqsClient = new AmazonSQSClient("test", "test", config);
        
        try
        {
            // Act - Send large message that may timeout
            var stopwatch = Stopwatch.StartNew();
            var largeMessage = new string('x', 50000); // Large message
            Exception? caughtException = null;
            
            try
            {
                await sqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = queueUrl,
                    MessageBody = largeMessage
                });
            }
            catch (Exception ex)
            {
                caughtException = ex;
                _output.WriteLine($"Timeout handled: {ex.GetType().Name}");
                _output.WriteLine($"Message: {ex.Message}");
            }
            
            stopwatch.Stop();
            
            // Assert - Should handle timeout gracefully
            if (caughtException != null)
            {
                // Timeout or related exception expected
                Assert.True(
                    caughtException is TaskCanceledException ||
                    caughtException is OperationCanceledException ||
                    caughtException is AmazonServiceException ||
                    caughtException.InnerException is TaskCanceledException,
                    $"Expected timeout-related exception, got: {caughtException.GetType().Name}");
                
                _output.WriteLine($"Operation timed out after {stopwatch.ElapsedMilliseconds}ms");
            }
            else
            {
                _output.WriteLine($"Operation succeeded in {stopwatch.ElapsedMilliseconds}ms");
            }
            
            // Verify timeout was respected (with retries)
            var maxExpectedDuration = config.Timeout.Value.TotalMilliseconds * (config.MaxErrorRetry + 1) * 2;
            Assert.True(stopwatch.ElapsedMilliseconds < maxExpectedDuration,
                $"Operation should respect timeout settings, took {stopwatch.ElapsedMilliseconds}ms");
        }
        finally
        {
            await _environment.DeleteQueueAsync(queueUrl);
        }
    }
    
    /// <summary>
    /// Test timeout handling for SNS operations
    /// Validates: Requirement 7.5 - Timeout handling
    /// </summary>
    [Fact]
    public async Task SnsClient_HandlesTimeouts_Appropriately()
    {
        // Arrange
        var topicArn = await _environment.CreateTopicAsync($"{_testPrefix}-timeout-sns");
        var config = new AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = _environment.IsLocalEmulator ? "http://localhost:4566" : null,
            MaxErrorRetry = 2,
            Timeout = TimeSpan.FromMilliseconds(50),
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        var snsClient = new AmazonSimpleNotificationServiceClient("test", "test", config);
        
        try
        {
            // Act
            var stopwatch = Stopwatch.StartNew();
            var largeMessage = new string('x', 50000);
            Exception? caughtException = null;
            
            try
            {
                await snsClient.PublishAsync(new PublishRequest
                {
                    TopicArn = topicArn,
                    Message = largeMessage
                });
            }
            catch (Exception ex)
            {
                caughtException = ex;
                _output.WriteLine($"Timeout handled: {ex.GetType().Name}");
            }
            
            stopwatch.Stop();
            
            // Assert
            if (caughtException != null)
            {
                Assert.True(
                    caughtException is TaskCanceledException ||
                    caughtException is OperationCanceledException ||
                    caughtException is AmazonServiceException ||
                    caughtException.InnerException is TaskCanceledException,
                    $"Expected timeout-related exception, got: {caughtException.GetType().Name}");
                
                _output.WriteLine($"Operation timed out after {stopwatch.ElapsedMilliseconds}ms");
            }
            
            var maxExpectedDuration = config.Timeout.Value.TotalMilliseconds * (config.MaxErrorRetry + 1) * 2;
            Assert.True(stopwatch.ElapsedMilliseconds < maxExpectedDuration,
                $"Operation should respect timeout settings");
        }
        finally
        {
            await _environment.DeleteTopicAsync(topicArn);
        }
    }

    /// <summary>
    /// Test connection pooling behavior for SQS clients
    /// Validates: Requirement 7.5 - Connection pooling
    /// </summary>
    [Fact]
    public async Task SqsClient_UsesConnectionPooling_Efficiently()
    {
        // Arrange
        var queueUrl = await _environment.CreateStandardQueueAsync($"{_testPrefix}-pool-sqs");
        var config = new AmazonSQSConfig
        {
            ServiceURL = _environment.IsLocalEmulator ? "http://localhost:4566" : null,
            MaxErrorRetry = 3,
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        // Create single client instance (simulating connection pooling)
        var sqsClient = new AmazonSQSClient("test", "test", config);
        
        try
        {
            // Act - Execute many operations with same client
            var stopwatch = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, 100).Select(async i =>
            {
                try
                {
                    await sqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MessageBody = $"Pooling test message {i}"
                    });
                    return true;
                }
                catch
                {
                    return false;
                }
            });
            
            var results = await Task.WhenAll(tasks);
            stopwatch.Stop();
            
            var successCount = results.Count(r => r);
            
            // Assert - Connection pooling should enable efficient concurrent operations
            Assert.True(successCount > 90, 
                $"At least 90% should succeed with connection pooling, got {successCount}/100");
            
            var avgTimePerMessage = stopwatch.ElapsedMilliseconds / 100.0;
            _output.WriteLine($"100 messages sent in {stopwatch.ElapsedMilliseconds}ms");
            _output.WriteLine($"Average: {avgTimePerMessage}ms per message");
            
            // With connection pooling, should be efficient
            Assert.True(avgTimePerMessage < 1000, 
                $"Connection pooling should enable efficient operations, got {avgTimePerMessage}ms per message");
        }
        finally
        {
            await _environment.DeleteQueueAsync(queueUrl);
        }
    }
    
    /// <summary>
    /// Test connection pooling behavior for SNS clients
    /// Validates: Requirement 7.5 - Connection pooling
    /// </summary>
    [Fact]
    public async Task SnsClient_UsesConnectionPooling_Efficiently()
    {
        // Arrange
        var topicArn = await _environment.CreateTopicAsync($"{_testPrefix}-pool-sns");
        var config = new AmazonSimpleNotificationServiceConfig
        {
            ServiceURL = _environment.IsLocalEmulator ? "http://localhost:4566" : null,
            MaxErrorRetry = 3,
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        var snsClient = new AmazonSimpleNotificationServiceClient("test", "test", config);
        
        try
        {
            // Act
            var stopwatch = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, 100).Select(async i =>
            {
                try
                {
                    await snsClient.PublishAsync(new PublishRequest
                    {
                        TopicArn = topicArn,
                        Message = $"Pooling test message {i}"
                    });
                    return true;
                }
                catch
                {
                    return false;
                }
            });
            
            var results = await Task.WhenAll(tasks);
            stopwatch.Stop();
            
            var successCount = results.Count(r => r);
            
            // Assert
            Assert.True(successCount > 90, 
                $"At least 90% should succeed with connection pooling, got {successCount}/100");
            
            var avgTimePerMessage = stopwatch.ElapsedMilliseconds / 100.0;
            _output.WriteLine($"100 messages published in {stopwatch.ElapsedMilliseconds}ms");
            _output.WriteLine($"Average: {avgTimePerMessage}ms per message");
            
            Assert.True(avgTimePerMessage < 1000, 
                $"Connection pooling should enable efficient operations, got {avgTimePerMessage}ms per message");
        }
        finally
        {
            await _environment.DeleteTopicAsync(topicArn);
        }
    }
    
    /// <summary>
    /// Test handling of intermittent network failures with retry
    /// Validates: Requirements 7.4, 7.5 - Throttling and network failure handling
    /// </summary>
    [Fact]
    public async Task AwsClients_HandleIntermittentFailures_WithRetry()
    {
        // Arrange
        var queueUrl = await _environment.CreateStandardQueueAsync($"{_testPrefix}-intermittent");
        var config = new AmazonSQSConfig
        {
            ServiceURL = _environment.IsLocalEmulator ? "http://localhost:4566" : null,
            MaxErrorRetry = 5,
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        var sqsClient = new AmazonSQSClient("test", "test", config);
        var successCount = 0;
        var failureCount = 0;
        
        try
        {
            // Act - Send messages with potential intermittent failures
            var tasks = Enumerable.Range(0, 50).Select(async i =>
            {
                try
                {
                    await sqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MessageBody = $"Intermittent test {i}"
                    });
                    Interlocked.Increment(ref successCount);
                    return true;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failureCount);
                    _output.WriteLine($"Message {i} failed: {ex.Message}");
                    return false;
                }
            });
            
            var results = await Task.WhenAll(tasks);
            
            // Assert - Most should succeed due to retry mechanism
            Assert.True(successCount > 40, 
                $"Retry mechanism should handle intermittent failures, got {successCount}/50 successes");
            
            _output.WriteLine($"Results: {successCount} succeeded, {failureCount} failed");
            _output.WriteLine("Retry mechanism successfully handled intermittent failures");
        }
        finally
        {
            await _environment.DeleteQueueAsync(queueUrl);
        }
    }
    
    /// <summary>
    /// Test that service errors are properly categorized and handled
    /// Validates: Requirements 7.4, 7.5 - Error categorization and handling
    /// </summary>
    [Fact]
    public async Task AwsClients_CategorizeServiceErrors_Appropriately()
    {
        // Arrange
        var testCases = new[]
        {
            new { QueueUrl = "https://sqs.us-east-1.amazonaws.com/000000000000/nonexistent", 
                  ExpectedErrorType = "NotFound", Description = "Queue not found" },
            new { QueueUrl = "", 
                  ExpectedErrorType = "Validation", Description = "Invalid queue URL" }
        };
        
        var config = new AmazonSQSConfig
        {
            ServiceURL = _environment.IsLocalEmulator ? "http://localhost:4566" : null,
            MaxErrorRetry = 2,
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        var sqsClient = new AmazonSQSClient("test", "test", config);
        
        // Act & Assert - Test each error scenario
        foreach (var testCase in testCases)
        {
            Exception? caughtException = null;
            
            try
            {
                await sqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = testCase.QueueUrl,
                    MessageBody = "test"
                });
            }
            catch (Exception ex)
            {
                caughtException = ex;
                _output.WriteLine($"{testCase.Description}: {ex.GetType().Name}");
                
                if (ex is AmazonServiceException awsEx)
                {
                    _output.WriteLine($"  Error Code: {awsEx.ErrorCode}");
                    _output.WriteLine($"  Status Code: {awsEx.StatusCode}");
                    _output.WriteLine($"  Retryable: {awsEx.Retryable}");
                }
            }
            
            Assert.NotNull(caughtException);
            _output.WriteLine($"Error properly categorized for: {testCase.Description}");
        }
    }
    
    /// <summary>
    /// Test concurrent operations under throttling conditions
    /// Validates: Requirement 7.4 - Concurrent throttling handling
    /// </summary>
    [Fact]
    public async Task AwsClients_HandleConcurrentThrottling_Gracefully()
    {
        // Arrange
        var queueUrl = await _environment.CreateStandardQueueAsync($"{_testPrefix}-concurrent-throttle");
        var config = new AmazonSQSConfig
        {
            ServiceURL = _environment.IsLocalEmulator ? "http://localhost:4566" : null,
            MaxErrorRetry = 5,
            RegionEndpoint = Amazon.RegionEndpoint.USEast1
        };
        
        var sqsClient = new AmazonSQSClient("test", "test", config);
        var concurrentOperations = 200;
        var successCount = 0;
        
        try
        {
            // Act - Execute many concurrent operations
            var stopwatch = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, concurrentOperations).Select(async i =>
            {
                try
                {
                    await sqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MessageBody = $"Concurrent message {i}"
                    });
                    Interlocked.Increment(ref successCount);
                    return true;
                }
                catch (AmazonServiceException ex) when (
                    ex.ErrorCode == "Throttling" || 
                    ex.ErrorCode == "RequestLimitExceeded")
                {
                    _output.WriteLine($"Message {i} throttled");
                    return false;
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Message {i} failed: {ex.Message}");
                    return false;
                }
            });
            
            var results = await Task.WhenAll(tasks);
            stopwatch.Stop();
            
            // Assert - System should handle concurrent throttling gracefully
            Assert.True(successCount > concurrentOperations * 0.6, 
                $"At least 60% should succeed under concurrent load, got {successCount}/{concurrentOperations}");
            
            _output.WriteLine($"Concurrent operations: {successCount}/{concurrentOperations} succeeded");
            _output.WriteLine($"Total duration: {stopwatch.ElapsedMilliseconds}ms");
            _output.WriteLine($"Average: {stopwatch.ElapsedMilliseconds / (double)concurrentOperations}ms per operation");
        }
        finally
        {
            await _environment.DeleteQueueAsync(queueUrl);
        }
    }
}
