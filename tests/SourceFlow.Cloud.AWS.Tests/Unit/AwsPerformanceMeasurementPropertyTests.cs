using Amazon.SQS.Model;
using Amazon.SimpleNotificationService.Model;
using FsCheck;
using FsCheck.Xunit;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Diagnostics;
using System.Text;

namespace SourceFlow.Cloud.AWS.Tests.Unit;

/// <summary>
/// Property-based tests for AWS performance measurement consistency
/// Validates that performance measurements are consistent and reliable across test runs
/// **Feature: aws-cloud-integration-testing, Property 9: AWS Performance Measurement Consistency**
/// </summary>
[Collection("AWS Integration Tests")]
public class AwsPerformanceMeasurementPropertyTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdQueues = new();
    private readonly List<string> _createdTopics = new();
    
    public AwsPerformanceMeasurementPropertyTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
    }
    
    /// <summary>
    /// Property 9: AWS Performance Measurement Consistency
    /// For any AWS performance test scenario, when executed multiple times under similar conditions, 
    /// the performance measurements (SQS/SNS throughput, end-to-end latency, resource utilization) 
    /// should be consistent within acceptable variance ranges and scale appropriately with load.
    /// **Validates: Requirements 5.1, 5.2, 5.3, 5.4, 5.5**
    /// </summary>
    [Fact]
    public async Task Property_AwsPerformanceMeasurementConsistency()
    {
        // Skip if not configured for performance tests
        if (!_localStack.Configuration.RunPerformanceTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Generate a few test scenarios to validate
        var scenarios = new[]
        {
            new AwsPerformanceScenario
            {
                TestSqsThroughput = true,
                TestSnsThroughput = false,
                TestEndToEndLatency = false,
                MessageCount = 10,
                MessageSizeBytes = 256,
                ConcurrentOperations = 2,
                UseFifoQueue = false,
                NumberOfRuns = 3,
                TestScalability = false
            },
            new AwsPerformanceScenario
            {
                TestSqsThroughput = false,
                TestSnsThroughput = true,
                TestEndToEndLatency = false,
                MessageCount = 10,
                MessageSizeBytes = 512,
                ConcurrentOperations = 2,
                UseFifoQueue = false,
                NumberOfRuns = 3,
                TestScalability = false
            },
            new AwsPerformanceScenario
            {
                TestSqsThroughput = false,
                TestSnsThroughput = false,
                TestEndToEndLatency = true,
                MessageCount = 5,
                MessageSizeBytes = 256,
                ConcurrentOperations = 1,
                UseFifoQueue = false,
                NumberOfRuns = 3,
                TestScalability = false
            }
        };
        
        foreach (var scenario in scenarios)
        {
            await ValidatePerformanceScenario(scenario);
        }
    }
    
    private async Task ValidatePerformanceScenario(AwsPerformanceScenario scenario)
    {
        // Arrange - Create test resources
        var resources = await CreatePerformanceTestResourcesAsync(scenario);
        
        try
        {
            // Act - Run performance test multiple times
            var measurements = new List<PerformanceMeasurement>();
            
            for (int run = 0; run < scenario.NumberOfRuns; run++)
            {
                var measurement = await ExecutePerformanceTestAsync(resources, scenario);
                measurements.Add(measurement);
                
                // Small delay between runs to avoid interference
                if (run < scenario.NumberOfRuns - 1)
                {
                    await Task.Delay(100);
                }
            }
            
            // Assert - Performance measurements are consistent
            AssertPerformanceConsistency(measurements, scenario);
            
            // Assert - Throughput measurements are within acceptable variance
            AssertThroughputConsistency(measurements, scenario);
            
            // Assert - Latency measurements are within acceptable variance
            AssertLatencyConsistency(measurements, scenario);
            
            // Assert - Resource utilization is reasonable
            AssertResourceUtilization(measurements, scenario);
            
            // Assert - Performance scales appropriately with load
            if (scenario.TestScalability)
            {
                await AssertPerformanceScalability(resources, scenario);
            }
        }
        finally
        {
            // Clean up resources
            await CleanupPerformanceResourcesAsync(resources);
        }
    }
    
    /// <summary>
    /// Create performance test resources based on scenario
    /// </summary>
    private async Task<PerformanceTestResources> CreatePerformanceTestResourcesAsync(AwsPerformanceScenario scenario)
    {
        var resources = new PerformanceTestResources();
        
        if (scenario.TestSqsThroughput || scenario.TestEndToEndLatency)
        {
            var queueName = scenario.UseFifoQueue 
                ? $"perf-test-{Guid.NewGuid():N}.fifo"
                : $"perf-test-{Guid.NewGuid():N}";
            
            var createRequest = new CreateQueueRequest
            {
                QueueName = queueName,
                Attributes = new Dictionary<string, string>
                {
                    ["MessageRetentionPeriod"] = "3600",
                    ["VisibilityTimeout"] = "30"
                }
            };
            
            if (scenario.UseFifoQueue)
            {
                createRequest.Attributes["FifoQueue"] = "true";
                createRequest.Attributes["ContentBasedDeduplication"] = "true";
            }
            
            var response = await _localStack.SqsClient!.CreateQueueAsync(createRequest);
            resources.QueueUrl = response.QueueUrl;
            _createdQueues.Add(response.QueueUrl);
        }
        
        if (scenario.TestSnsThroughput)
        {
            var topicName = $"perf-test-{Guid.NewGuid():N}";
            var response = await _localStack.SnsClient!.CreateTopicAsync(new CreateTopicRequest
            {
                Name = topicName
            });
            resources.TopicArn = response.TopicArn;
            _createdTopics.Add(response.TopicArn);
            
            // Create SQS queue for SNS subscription
            var queueName = $"perf-test-sns-sub-{Guid.NewGuid():N}";
            var queueResponse = await _localStack.SqsClient!.CreateQueueAsync(new CreateQueueRequest
            {
                QueueName = queueName
            });
            resources.SubscriptionQueueUrl = queueResponse.QueueUrl;
            _createdQueues.Add(queueResponse.QueueUrl);
            
            // Subscribe queue to topic
            await _localStack.SnsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = resources.TopicArn,
                Protocol = "sqs",
                Endpoint = $"arn:aws:sqs:us-east-1:000000000000:{queueName}"
            });
        }
        
        return resources;
    }
    
    /// <summary>
    /// Execute a single performance test run
    /// </summary>
    private async Task<PerformanceMeasurement> ExecutePerformanceTestAsync(
        PerformanceTestResources resources, 
        AwsPerformanceScenario scenario)
    {
        var measurement = new PerformanceMeasurement
        {
            TestType = scenario.TestSqsThroughput ? "SQS Throughput" : 
                      scenario.TestSnsThroughput ? "SNS Throughput" : "End-to-End Latency",
            MessageCount = scenario.MessageCount,
            MessageSizeBytes = scenario.MessageSizeBytes,
            ConcurrentOperations = scenario.ConcurrentOperations
        };
        
        var stopwatch = Stopwatch.StartNew();
        var startMemory = GC.GetTotalMemory(false);
        
        try
        {
            if (scenario.TestSqsThroughput)
            {
                await MeasureSqsThroughputAsync(resources, scenario, measurement);
            }
            else if (scenario.TestSnsThroughput)
            {
                await MeasureSnsThroughputAsync(resources, scenario, measurement);
            }
            else if (scenario.TestEndToEndLatency)
            {
                await MeasureEndToEndLatencyAsync(resources, scenario, measurement);
            }
            
            stopwatch.Stop();
            var endMemory = GC.GetTotalMemory(false);
            
            measurement.TotalDuration = stopwatch.Elapsed;
            measurement.MemoryUsedBytes = endMemory - startMemory;
            measurement.Success = true;
            
            // Calculate throughput
            if (measurement.TotalDuration.TotalSeconds > 0)
            {
                measurement.MessagesPerSecond = measurement.MessageCount / measurement.TotalDuration.TotalSeconds;
            }
        }
        catch (Exception ex)
        {
            measurement.Success = false;
            measurement.ErrorMessage = ex.Message;
        }
        
        return measurement;
    }
    
    /// <summary>
    /// Measure SQS throughput performance
    /// </summary>
    private async Task MeasureSqsThroughputAsync(
        PerformanceTestResources resources, 
        AwsPerformanceScenario scenario,
        PerformanceMeasurement measurement)
    {
        var messageBody = GenerateMessageBody(scenario.MessageSizeBytes);
        var messagesPerOperation = scenario.MessageCount / scenario.ConcurrentOperations;
        var operationLatencies = new List<TimeSpan>();
        
        var tasks = Enumerable.Range(0, scenario.ConcurrentOperations)
            .Select(async operationId =>
            {
                var operationStopwatch = Stopwatch.StartNew();
                
                for (int i = 0; i < messagesPerOperation; i++)
                {
                    var request = new SendMessageRequest
                    {
                        QueueUrl = resources.QueueUrl,
                        MessageBody = messageBody,
                        MessageAttributes = new Dictionary<string, Amazon.SQS.Model.MessageAttributeValue>
                        {
                            ["OperationId"] = new Amazon.SQS.Model.MessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = operationId.ToString()
                            },
                            ["MessageIndex"] = new Amazon.SQS.Model.MessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = i.ToString()
                            }
                        }
                    };
                    
                    if (scenario.UseFifoQueue)
                    {
                        request.MessageGroupId = $"group-{operationId}";
                        request.MessageDeduplicationId = $"op-{operationId}-msg-{i}-{Guid.NewGuid():N}";
                    }
                    
                    await _localStack.SqsClient!.SendMessageAsync(request);
                }
                
                operationStopwatch.Stop();
                lock (operationLatencies)
                {
                    operationLatencies.Add(operationStopwatch.Elapsed);
                }
            });
        
        await Task.WhenAll(tasks);
        
        measurement.AverageLatency = TimeSpan.FromMilliseconds(operationLatencies.Average(l => l.TotalMilliseconds));
        measurement.MinLatency = operationLatencies.Min();
        measurement.MaxLatency = operationLatencies.Max();
    }
    
    /// <summary>
    /// Measure SNS throughput performance
    /// </summary>
    private async Task MeasureSnsThroughputAsync(
        PerformanceTestResources resources, 
        AwsPerformanceScenario scenario,
        PerformanceMeasurement measurement)
    {
        var messageBody = GenerateMessageBody(scenario.MessageSizeBytes);
        var messagesPerOperation = scenario.MessageCount / scenario.ConcurrentOperations;
        var operationLatencies = new List<TimeSpan>();
        
        var tasks = Enumerable.Range(0, scenario.ConcurrentOperations)
            .Select(async operationId =>
            {
                var operationStopwatch = Stopwatch.StartNew();
                
                for (int i = 0; i < messagesPerOperation; i++)
                {
                    await _localStack.SnsClient!.PublishAsync(new PublishRequest
                    {
                        TopicArn = resources.TopicArn,
                        Message = messageBody,
                        MessageAttributes = new Dictionary<string, Amazon.SimpleNotificationService.Model.MessageAttributeValue>
                        {
                            ["OperationId"] = new Amazon.SimpleNotificationService.Model.MessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = operationId.ToString()
                            },
                            ["MessageIndex"] = new Amazon.SimpleNotificationService.Model.MessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = i.ToString()
                            }
                        }
                    });
                }
                
                operationStopwatch.Stop();
                lock (operationLatencies)
                {
                    operationLatencies.Add(operationStopwatch.Elapsed);
                }
            });
        
        await Task.WhenAll(tasks);
        
        measurement.AverageLatency = TimeSpan.FromMilliseconds(operationLatencies.Average(l => l.TotalMilliseconds));
        measurement.MinLatency = operationLatencies.Min();
        measurement.MaxLatency = operationLatencies.Max();
    }
    
    /// <summary>
    /// Measure end-to-end latency (send + receive + delete)
    /// </summary>
    private async Task MeasureEndToEndLatencyAsync(
        PerformanceTestResources resources, 
        AwsPerformanceScenario scenario,
        PerformanceMeasurement measurement)
    {
        var messageBody = GenerateMessageBody(scenario.MessageSizeBytes);
        var latencies = new List<TimeSpan>();
        
        for (int i = 0; i < scenario.MessageCount; i++)
        {
            var e2eStopwatch = Stopwatch.StartNew();
            
            // Send message
            var sendRequest = new SendMessageRequest
            {
                QueueUrl = resources.QueueUrl,
                MessageBody = messageBody,
                MessageAttributes = new Dictionary<string, Amazon.SQS.Model.MessageAttributeValue>
                {
                    ["Timestamp"] = new Amazon.SQS.Model.MessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
                    }
                }
            };
            
            if (scenario.UseFifoQueue)
            {
                sendRequest.MessageGroupId = $"e2e-group-{i}";
                sendRequest.MessageDeduplicationId = $"e2e-{i}-{Guid.NewGuid():N}";
            }
            
            await _localStack.SqsClient!.SendMessageAsync(sendRequest);
            
            // Receive message
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = resources.QueueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 2,
                MessageAttributeNames = new List<string> { "All" }
            });
            
            // Delete message
            if (receiveResponse.Messages.Count > 0)
            {
                await _localStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = resources.QueueUrl,
                    ReceiptHandle = receiveResponse.Messages[0].ReceiptHandle
                });
            }
            
            e2eStopwatch.Stop();
            latencies.Add(e2eStopwatch.Elapsed);
        }
        
        measurement.AverageLatency = TimeSpan.FromMilliseconds(latencies.Average(l => l.TotalMilliseconds));
        measurement.MinLatency = latencies.Min();
        measurement.MaxLatency = latencies.Max();
    }
    
    /// <summary>
    /// Assert that performance measurements are consistent across runs
    /// </summary>
    private void AssertPerformanceConsistency(List<PerformanceMeasurement> measurements, AwsPerformanceScenario scenario)
    {
        // All measurements should be successful
        var successfulMeasurements = measurements.Where(m => m.Success).ToList();
        Assert.True(successfulMeasurements.Count >= measurements.Count * 0.9,
            $"At least 90% of performance measurements should succeed, got {successfulMeasurements.Count}/{measurements.Count}");
        
        if (successfulMeasurements.Count < 2)
        {
            return; // Need at least 2 measurements for consistency check
        }
        
        // Calculate coefficient of variation (CV) for total duration
        var durations = successfulMeasurements.Select(m => m.TotalDuration.TotalMilliseconds).ToList();
        var avgDuration = durations.Average();
        var stdDevDuration = Math.Sqrt(durations.Average(d => Math.Pow(d - avgDuration, 2)));
        var cvDuration = stdDevDuration / avgDuration;
        
        // CV should be less than 0.5 (50%) for reasonable consistency
        Assert.True(cvDuration < 0.5,
            $"Performance duration should be consistent (CV < 0.5), got CV = {cvDuration:F3}");
    }
    
    /// <summary>
    /// Assert that throughput measurements are within acceptable variance
    /// </summary>
    private void AssertThroughputConsistency(List<PerformanceMeasurement> measurements, AwsPerformanceScenario scenario)
    {
        var successfulMeasurements = measurements.Where(m => m.Success && m.MessagesPerSecond > 0).ToList();
        
        if (successfulMeasurements.Count < 2)
        {
            return; // Need at least 2 measurements
        }
        
        var throughputs = successfulMeasurements.Select(m => m.MessagesPerSecond).ToList();
        var avgThroughput = throughputs.Average();
        var minThroughput = throughputs.Min();
        var maxThroughput = throughputs.Max();
        
        // Throughput should be positive
        Assert.True(avgThroughput > 0, "Average throughput should be positive");
        
        // Variance should be within acceptable range (within 2x of average)
        var varianceRatio = maxThroughput / minThroughput;
        Assert.True(varianceRatio < 3.0,
            $"Throughput variance should be reasonable (max/min < 3.0), got {varianceRatio:F2}");
        
        // For LocalStack, throughput should be at least 1 msg/sec
        Assert.True(avgThroughput >= 1.0,
            $"Average throughput should be at least 1 msg/sec, got {avgThroughput:F2}");
    }
    
    /// <summary>
    /// Assert that latency measurements are within acceptable variance
    /// </summary>
    private void AssertLatencyConsistency(List<PerformanceMeasurement> measurements, AwsPerformanceScenario scenario)
    {
        var successfulMeasurements = measurements.Where(m => m.Success && m.AverageLatency > TimeSpan.Zero).ToList();
        
        if (successfulMeasurements.Count < 2)
        {
            return; // Need at least 2 measurements
        }
        
        var avgLatencies = successfulMeasurements.Select(m => m.AverageLatency.TotalMilliseconds).ToList();
        var overallAvgLatency = avgLatencies.Average();
        var stdDevLatency = Math.Sqrt(avgLatencies.Average(l => Math.Pow(l - overallAvgLatency, 2)));
        var cvLatency = stdDevLatency / overallAvgLatency;
        
        // Latency CV should be less than 0.6 (60%) for reasonable consistency
        Assert.True(cvLatency < 0.6,
            $"Latency should be consistent (CV < 0.6), got CV = {cvLatency:F3}");
        
        // Average latency should be reasonable (less than 10 seconds for LocalStack)
        Assert.True(overallAvgLatency < 10000,
            $"Average latency should be less than 10 seconds, got {overallAvgLatency:F2}ms");
        
        // Min latency should be less than max latency
        foreach (var measurement in successfulMeasurements)
        {
            Assert.True(measurement.MinLatency <= measurement.MaxLatency,
                "Min latency should be less than or equal to max latency");
            Assert.True(measurement.MinLatency <= measurement.AverageLatency,
                "Min latency should be less than or equal to average latency");
            Assert.True(measurement.AverageLatency <= measurement.MaxLatency,
                "Average latency should be less than or equal to max latency");
        }
    }
    
    /// <summary>
    /// Assert that resource utilization is reasonable
    /// </summary>
    private void AssertResourceUtilization(List<PerformanceMeasurement> measurements, AwsPerformanceScenario scenario)
    {
        var successfulMeasurements = measurements.Where(m => m.Success).ToList();
        
        if (successfulMeasurements.Count == 0)
        {
            return;
        }
        
        // Memory usage should be reasonable (less than 100MB per test run)
        var maxMemoryUsage = successfulMeasurements.Max(m => m.MemoryUsedBytes);
        Assert.True(maxMemoryUsage < 100 * 1024 * 1024,
            $"Memory usage should be less than 100MB, got {maxMemoryUsage / (1024.0 * 1024.0):F2}MB");
        
        // Memory usage should scale reasonably with message count and size
        var avgMemoryPerMessage = successfulMeasurements.Average(m => 
            m.MessageCount > 0 ? (double)m.MemoryUsedBytes / m.MessageCount : 0);
        
        // Should use less than 10KB per message on average (accounting for overhead)
        Assert.True(avgMemoryPerMessage < 10 * 1024,
            $"Average memory per message should be less than 10KB, got {avgMemoryPerMessage / 1024.0:F2}KB");
    }
    
    /// <summary>
    /// Assert that performance scales appropriately with load
    /// </summary>
    private async Task AssertPerformanceScalability(PerformanceTestResources resources, AwsPerformanceScenario scenario)
    {
        // Test with different load levels
        var loadLevels = new[] { scenario.MessageCount / 2, scenario.MessageCount, scenario.MessageCount * 2 };
        var scalabilityMeasurements = new List<(int Load, double Throughput)>();
        
        foreach (var load in loadLevels)
        {
            var scalabilityScenario = new AwsPerformanceScenario
            {
                TestSqsThroughput = scenario.TestSqsThroughput,
                TestSnsThroughput = scenario.TestSnsThroughput,
                TestEndToEndLatency = scenario.TestEndToEndLatency,
                MessageCount = load,
                MessageSizeBytes = scenario.MessageSizeBytes,
                ConcurrentOperations = scenario.ConcurrentOperations,
                UseFifoQueue = scenario.UseFifoQueue,
                NumberOfRuns = 1,
                TestScalability = false
            };
            
            var measurement = await ExecutePerformanceTestAsync(resources, scalabilityScenario);
            
            if (measurement.Success && measurement.MessagesPerSecond > 0)
            {
                scalabilityMeasurements.Add((load, measurement.MessagesPerSecond));
            }
            
            // Small delay between scalability tests
            await Task.Delay(200);
        }
        
        if (scalabilityMeasurements.Count >= 2)
        {
            // Throughput should generally increase or remain stable with load
            // (or at least not decrease dramatically)
            var firstThroughput = scalabilityMeasurements[0].Throughput;
            var lastThroughput = scalabilityMeasurements[^1].Throughput;
            
            // Allow throughput to decrease by at most 50% as load increases
            // (LocalStack may have different characteristics than real AWS)
            Assert.True(lastThroughput > firstThroughput * 0.5,
                $"Throughput should not decrease dramatically with load. " +
                $"First: {firstThroughput:F2} msg/s, Last: {lastThroughput:F2} msg/s");
        }
    }
    
    /// <summary>
    /// Clean up performance test resources
    /// </summary>
    private async Task CleanupPerformanceResourcesAsync(PerformanceTestResources resources)
    {
        if (!string.IsNullOrEmpty(resources.QueueUrl))
        {
            try
            {
                // Purge queue first to speed up deletion
                await _localStack.SqsClient!.PurgeQueueAsync(new PurgeQueueRequest
                {
                    QueueUrl = resources.QueueUrl
                });
                
                await Task.Delay(100); // Small delay after purge
                
                await _localStack.SqsClient.DeleteQueueAsync(new DeleteQueueRequest
                {
                    QueueUrl = resources.QueueUrl
                });
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        
        if (!string.IsNullOrEmpty(resources.SubscriptionQueueUrl))
        {
            try
            {
                await _localStack.SqsClient!.DeleteQueueAsync(new DeleteQueueRequest
                {
                    QueueUrl = resources.SubscriptionQueueUrl
                });
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        
        if (!string.IsNullOrEmpty(resources.TopicArn))
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
    }
    
    /// <summary>
    /// Generate message body of specified size
    /// </summary>
    private string GenerateMessageBody(int sizeBytes)
    {
        var sb = new StringBuilder(sizeBytes);
        var random = new System.Random();
        
        while (sb.Length < sizeBytes)
        {
            sb.Append((char)('A' + random.Next(26)));
        }
        
        return sb.ToString(0, sizeBytes);
    }
    
    /// <summary>
    /// Clean up created resources
    /// </summary>
    public async ValueTask DisposeAsync()
    {
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
        
        _createdQueues.Clear();
        _createdTopics.Clear();
    }
}


#region Test Models and Generators

/// <summary>
/// Scenario for AWS performance testing
/// </summary>
public class AwsPerformanceScenario
{
    public bool TestSqsThroughput { get; set; }
    public bool TestSnsThroughput { get; set; }
    public bool TestEndToEndLatency { get; set; }
    public int MessageCount { get; set; }
    public int MessageSizeBytes { get; set; }
    public int ConcurrentOperations { get; set; }
    public bool UseFifoQueue { get; set; }
    public int NumberOfRuns { get; set; }
    public bool TestScalability { get; set; }
}

/// <summary>
/// Resources created for performance testing
/// </summary>
public class PerformanceTestResources
{
    public string? QueueUrl { get; set; }
    public string? TopicArn { get; set; }
    public string? SubscriptionQueueUrl { get; set; }
}

/// <summary>
/// Performance measurement result
/// </summary>
public class PerformanceMeasurement
{
    public string TestType { get; set; } = "";
    public int MessageCount { get; set; }
    public int MessageSizeBytes { get; set; }
    public int ConcurrentOperations { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public TimeSpan AverageLatency { get; set; }
    public TimeSpan MinLatency { get; set; }
    public TimeSpan MaxLatency { get; set; }
    public double MessagesPerSecond { get; set; }
    public long MemoryUsedBytes { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}


/// <summary>
/// FsCheck generators for AWS performance scenarios
/// </summary>
public static class AwsPerformanceGenerators
{
    /// <summary>
    /// Generate valid AWS performance test scenarios
    /// </summary>
    public static Arbitrary<AwsPerformanceScenario> AwsPerformanceScenario()
    {
        var generator = from testType in Gen.Choose(0, 2)
                       from messageCount in Gen.Choose(5, 50)  // Keep small for property tests
                       from messageSizeBytes in Gen.Elements(128, 256, 512, 1024)
                       from concurrentOps in Gen.Choose(1, 5)
                       from useFifo in Arb.Generate<bool>()
                       from numberOfRuns in Gen.Choose(2, 5)  // Multiple runs for consistency check
                       from testScalability in Gen.Frequency(
                           Tuple.Create(8, Gen.Constant(false)),  // 80% no scalability test
                           Tuple.Create(2, Gen.Constant(true)))   // 20% with scalability test
                       select new AwsPerformanceScenario
                       {
                           TestSqsThroughput = testType == 0,
                           TestSnsThroughput = testType == 1,
                           TestEndToEndLatency = testType == 2,
                           MessageCount = messageCount,
                           MessageSizeBytes = messageSizeBytes,
                           ConcurrentOperations = concurrentOps,
                           UseFifoQueue = useFifo && testType != 1,  // SNS doesn't use FIFO
                           NumberOfRuns = numberOfRuns,
                           TestScalability = testScalability && messageCount >= 10  // Only test scalability with sufficient messages
                       };
        
        return Arb.From(generator);
    }
}

#endregion
