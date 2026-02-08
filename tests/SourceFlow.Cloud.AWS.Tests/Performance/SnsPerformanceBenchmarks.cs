using System.Diagnostics;
using System.Text;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using BenchmarkDotNet.Attributes;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace SourceFlow.Cloud.AWS.Tests.Performance;

/// <summary>
/// Enhanced performance benchmarks for SNS operations
/// Validates Requirements 5.2, 5.3 - SNS throughput and end-to-end latency testing
/// 
/// This benchmark suite provides comprehensive performance testing for:
/// - Event publishing rate testing
/// - Fan-out delivery performance with multiple subscribers
/// - SNS-to-SQS delivery latency
/// - Performance impact of message filtering
/// - End-to-end latency including network overhead
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class SnsPerformanceBenchmarks : PerformanceBenchmarkBase
{
    private string? _topicArn;
    private readonly List<string> _subscriberQueueUrls = new();
    private readonly List<string> _subscriptionArns = new();
    
    // Benchmark parameters
    [Params(1, 5, 10)]
    public int ConcurrentPublishers { get; set; }
    
    [Params(100, 500, 1000)]
    public int MessageCount { get; set; }
    
    [Params(256, 1024, 4096)]
    public int MessageSizeBytes { get; set; }
    
    [Params(1, 3, 5)]
    public int SubscriberCount { get; set; }
    
    [GlobalSetup]
    public override async Task GlobalSetup()
    {
        await base.GlobalSetup();
        
        if (LocalStack?.SnsClient != null && LocalStack?.SqsClient != null && LocalStack.Configuration.RunPerformanceTests)
        {
            // Create an SNS topic for performance testing
            var topicName = $"perf-test-topic-{Guid.NewGuid():N}";
            var topicResponse = await LocalStack.SnsClient.CreateTopicAsync(new CreateTopicRequest
            {
                Name = topicName,
                Attributes = new Dictionary<string, string>
                {
                    ["DisplayName"] = "Performance Test Topic"
                }
            });
            _topicArn = topicResponse.TopicArn;
            
            // Create SQS queues as subscribers
            for (int i = 0; i < SubscriberCount; i++)
            {
                var queueName = $"perf-test-subscriber-{i}-{Guid.NewGuid():N}";
                var queueResponse = await LocalStack.SqsClient.CreateQueueAsync(new CreateQueueRequest
                {
                    QueueName = queueName,
                    Attributes = new Dictionary<string, string>
                    {
                        ["MessageRetentionPeriod"] = "3600", // 1 hour
                        ["VisibilityTimeout"] = "30"
                    }
                });
                _subscriberQueueUrls.Add(queueResponse.QueueUrl);
                
                // Get queue ARN for subscription
                var queueAttributes = await LocalStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
                {
                    QueueUrl = queueResponse.QueueUrl,
                    AttributeNames = new List<string> { "QueueArn" }
                });
                var queueArn = queueAttributes.Attributes["QueueArn"];
                
                // Subscribe queue to topic
                var subscriptionResponse = await LocalStack.SnsClient.SubscribeAsync(new SubscribeRequest
                {
                    TopicArn = _topicArn,
                    Protocol = "sqs",
                    Endpoint = queueArn
                });
                _subscriptionArns.Add(subscriptionResponse.SubscriptionArn);
            }
        }
    }
    
    [GlobalCleanup]
    public override async Task GlobalCleanup()
    {
        if (LocalStack?.SnsClient != null && LocalStack?.SqsClient != null)
        {
            // Unsubscribe all subscriptions
            foreach (var subscriptionArn in _subscriptionArns)
            {
                try
                {
                    await LocalStack.SnsClient.UnsubscribeAsync(new UnsubscribeRequest
                    {
                        SubscriptionArn = subscriptionArn
                    });
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
            
            // Delete all subscriber queues
            foreach (var queueUrl in _subscriberQueueUrls)
            {
                try
                {
                    await LocalStack.SqsClient.DeleteQueueAsync(queueUrl);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
            
            // Delete the topic
            if (!string.IsNullOrEmpty(_topicArn))
            {
                try
                {
                    await LocalStack.SnsClient.DeleteTopicAsync(new DeleteTopicRequest
                    {
                        TopicArn = _topicArn
                    });
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
        
        await base.GlobalCleanup();
    }
    
    /// <summary>
    /// Benchmark: Event publishing rate with single publisher
    /// Measures messages per second for SNS topic publishing
    /// </summary>
    [Benchmark(Description = "SNS Topic - Single Publisher Throughput")]
    public async Task SnsTopicSinglePublisherThroughput()
    {
        if (LocalStack?.SnsClient == null || string.IsNullOrEmpty(_topicArn))
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        
        for (int i = 0; i < MessageCount; i++)
        {
            await LocalStack.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = _topicArn,
                Message = messageBody,
                MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                {
                    ["MessageIndex"] = new SnsMessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    }
                }
            });
        }
    }
    
    /// <summary>
    /// Benchmark: Event publishing rate with concurrent publishers
    /// Measures messages per second with multiple concurrent publishers
    /// </summary>
    [Benchmark(Description = "SNS Topic - Concurrent Publishers Throughput")]
    public async Task SnsTopicConcurrentPublishersThroughput()
    {
        if (LocalStack?.SnsClient == null || string.IsNullOrEmpty(_topicArn))
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        var messagesPerPublisher = MessageCount / ConcurrentPublishers;
        
        var tasks = Enumerable.Range(0, ConcurrentPublishers)
            .Select(async publisherId =>
            {
                for (int i = 0; i < messagesPerPublisher; i++)
                {
                    await LocalStack.SnsClient.PublishAsync(new PublishRequest
                    {
                        TopicArn = _topicArn,
                        Message = messageBody,
                        MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                        {
                            ["PublisherId"] = new SnsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = publisherId.ToString()
                            },
                            ["MessageIndex"] = new SnsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = i.ToString()
                            }
                        }
                    });
                }
            });
        
        await Task.WhenAll(tasks);
    }
    
    /// <summary>
    /// Benchmark: Fan-out delivery performance with multiple subscribers
    /// Measures SNS-to-SQS delivery latency and fan-out efficiency
    /// </summary>
    [Benchmark(Description = "SNS Fan-Out - Multiple Subscribers Delivery")]
    public async Task SnsFanOutDeliveryPerformance()
    {
        if (LocalStack?.SnsClient == null || LocalStack?.SqsClient == null || 
            string.IsNullOrEmpty(_topicArn) || _subscriberQueueUrls.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        var publishCount = Math.Min(MessageCount, 100); // Limit for fan-out test
        
        // Publish messages to topic
        for (int i = 0; i < publishCount; i++)
        {
            await LocalStack.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = _topicArn,
                Message = messageBody,
                MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                {
                    ["MessageId"] = new SnsMessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = Guid.NewGuid().ToString()
                    },
                    ["Timestamp"] = new SnsMessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
                    }
                }
            });
        }
        
        // Wait a bit for message propagation
        await Task.Delay(1000);
        
        // Verify delivery to all subscribers
        var receiveTasks = _subscriberQueueUrls.Select(async queueUrl =>
        {
            var receivedCount = 0;
            var maxAttempts = 10;
            var attempts = 0;
            
            while (receivedCount < publishCount && attempts < maxAttempts)
            {
                var response = await LocalStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 1,
                    MessageAttributeNames = new List<string> { "All" }
                });
                
                if (response.Messages.Count > 0)
                {
                    // Delete received messages
                    var deleteTasks = response.Messages.Select(msg =>
                        LocalStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                        {
                            QueueUrl = queueUrl,
                            ReceiptHandle = msg.ReceiptHandle
                        }));
                    
                    await Task.WhenAll(deleteTasks);
                    receivedCount += response.Messages.Count;
                }
                
                attempts++;
            }
            
            return receivedCount;
        });
        
        await Task.WhenAll(receiveTasks);
    }
    
    /// <summary>
    /// Benchmark: SNS-to-SQS delivery latency
    /// Measures end-to-end latency from SNS publish to SQS receive
    /// </summary>
    [Benchmark(Description = "SNS-to-SQS - End-to-End Delivery Latency")]
    public async Task SnsToSqsDeliveryLatency()
    {
        if (LocalStack?.SnsClient == null || LocalStack?.SqsClient == null || 
            string.IsNullOrEmpty(_topicArn) || _subscriberQueueUrls.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        var queueUrl = _subscriberQueueUrls[0]; // Use first subscriber
        
        // Publish message with timestamp
        var publishTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await LocalStack.SnsClient.PublishAsync(new PublishRequest
        {
            TopicArn = _topicArn,
            Message = messageBody,
            MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
            {
                ["PublishTimestamp"] = new SnsMessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = publishTimestamp.ToString()
                },
                ["MessageId"] = new SnsMessageAttributeValue
                {
                    DataType = "String",
                    StringValue = Guid.NewGuid().ToString()
                }
            }
        });
        
        // Receive message from subscriber queue
        var maxAttempts = 10;
        var attempts = 0;
        
        while (attempts < maxAttempts)
        {
            var response = await LocalStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 2,
                MessageAttributeNames = new List<string> { "All" }
            });
            
            if (response.Messages.Count > 0)
            {
                var message = response.Messages[0];
                
                // Delete message
                await LocalStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle
                });
                
                break;
            }
            
            attempts++;
        }
    }
    
    /// <summary>
    /// Benchmark: Message filtering performance impact
    /// Measures the performance overhead of SNS message filtering
    /// </summary>
    [Benchmark(Description = "SNS Filtering - Performance Impact")]
    public async Task SnsMessageFilteringPerformanceImpact()
    {
        if (LocalStack?.SnsClient == null || LocalStack?.SqsClient == null || 
            string.IsNullOrEmpty(_topicArn))
            return;
        
        // Create a filtered subscription
        var filterQueueName = $"perf-test-filtered-{Guid.NewGuid():N}";
        var filterQueueResponse = await LocalStack.SqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = filterQueueName,
            Attributes = new Dictionary<string, string>
            {
                ["MessageRetentionPeriod"] = "3600",
                ["VisibilityTimeout"] = "30"
            }
        });
        var filterQueueUrl = filterQueueResponse.QueueUrl;
        
        try
        {
            // Get queue ARN
            var queueAttributes = await LocalStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = filterQueueUrl,
                AttributeNames = new List<string> { "QueueArn" }
            });
            var queueArn = queueAttributes.Attributes["QueueArn"];
            
            // Subscribe with filter policy
            var filterPolicy = @"{
                ""EventType"": [""OrderCreated"", ""OrderUpdated""],
                ""Priority"": [{""numeric"": ["">="", 5]}]
            }";
            
            var subscriptionResponse = await LocalStack.SnsClient.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = _topicArn,
                Protocol = "sqs",
                Endpoint = queueArn,
                Attributes = new Dictionary<string, string>
                {
                    ["FilterPolicy"] = filterPolicy
                }
            });
            
            var messageBody = GenerateMessageBody(MessageSizeBytes);
            var publishCount = Math.Min(MessageCount, 100); // Limit for filtering test
            
            // Publish messages with varying attributes (some match filter, some don't)
            for (int i = 0; i < publishCount; i++)
            {
                var eventType = i % 3 == 0 ? "OrderCreated" : (i % 3 == 1 ? "OrderUpdated" : "OrderDeleted");
                var priority = i % 10;
                
                await LocalStack.SnsClient.PublishAsync(new PublishRequest
                {
                    TopicArn = _topicArn,
                    Message = messageBody,
                    MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                    {
                        ["EventType"] = new SnsMessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = eventType
                        },
                        ["Priority"] = new SnsMessageAttributeValue
                        {
                            DataType = "Number",
                            StringValue = priority.ToString()
                        },
                        ["MessageIndex"] = new SnsMessageAttributeValue
                        {
                            DataType = "Number",
                            StringValue = i.ToString()
                        }
                    }
                });
            }
            
            // Wait for message propagation
            await Task.Delay(1000);
            
            // Receive filtered messages
            var receivedCount = 0;
            var maxAttempts = 10;
            var attempts = 0;
            
            while (attempts < maxAttempts)
            {
                var response = await LocalStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = filterQueueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 1
                });
                
                if (response.Messages.Count > 0)
                {
                    // Delete received messages
                    var deleteTasks = response.Messages.Select(msg =>
                        LocalStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                        {
                            QueueUrl = filterQueueUrl,
                            ReceiptHandle = msg.ReceiptHandle
                        }));
                    
                    await Task.WhenAll(deleteTasks);
                    receivedCount += response.Messages.Count;
                }
                else
                {
                    break;
                }
                
                attempts++;
            }
            
            // Cleanup subscription
            await LocalStack.SnsClient.UnsubscribeAsync(new UnsubscribeRequest
            {
                SubscriptionArn = subscriptionResponse.SubscriptionArn
            });
        }
        finally
        {
            // Cleanup filter queue
            try
            {
                await LocalStack.SqsClient.DeleteQueueAsync(filterQueueUrl);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
    
    /// <summary>
    /// Benchmark: Message attributes performance overhead for SNS
    /// Measures the performance impact of including message attributes in SNS publish
    /// </summary>
    [Benchmark(Description = "SNS Topic - Message Attributes Overhead")]
    public async Task SnsMessageAttributesOverhead()
    {
        if (LocalStack?.SnsClient == null || string.IsNullOrEmpty(_topicArn))
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        
        for (int i = 0; i < MessageCount; i++)
        {
            await LocalStack.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = _topicArn,
                Message = messageBody,
                MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                {
                    ["EventType"] = new SnsMessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "TestEvent"
                    },
                    ["EntityId"] = new SnsMessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = "12345"
                    },
                    ["SequenceNo"] = new SnsMessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    },
                    ["Timestamp"] = new SnsMessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
                    },
                    ["CorrelationId"] = new SnsMessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = Guid.NewGuid().ToString()
                    }
                }
            });
        }
    }
    
    /// <summary>
    /// Benchmark: Concurrent fan-out with high subscriber count
    /// Measures scalability of SNS fan-out with multiple concurrent publishers and subscribers
    /// </summary>
    [Benchmark(Description = "SNS Fan-Out - Concurrent Publishers and Subscribers")]
    public async Task SnsConcurrentFanOutScalability()
    {
        if (LocalStack?.SnsClient == null || LocalStack?.SqsClient == null || 
            string.IsNullOrEmpty(_topicArn) || _subscriberQueueUrls.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        var messagesPerPublisher = Math.Min(MessageCount / ConcurrentPublishers, 50); // Limit for scalability test
        
        // Publish messages concurrently
        var publishTasks = Enumerable.Range(0, ConcurrentPublishers)
            .Select(async publisherId =>
            {
                for (int i = 0; i < messagesPerPublisher; i++)
                {
                    await LocalStack.SnsClient.PublishAsync(new PublishRequest
                    {
                        TopicArn = _topicArn,
                        Message = messageBody,
                        MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                        {
                            ["PublisherId"] = new SnsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = publisherId.ToString()
                            },
                            ["MessageIndex"] = new SnsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = i.ToString()
                            }
                        }
                    });
                }
            });
        
        await Task.WhenAll(publishTasks);
        
        // Wait for message propagation
        await Task.Delay(2000);
        
        // Receive messages from all subscribers concurrently
        var receiveTasks = _subscriberQueueUrls.Select(async queueUrl =>
        {
            var receivedCount = 0;
            var maxAttempts = 15;
            var attempts = 0;
            
            while (attempts < maxAttempts)
            {
                var response = await LocalStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 1
                });
                
                if (response.Messages.Count > 0)
                {
                    // Delete received messages
                    var deleteTasks = response.Messages.Select(msg =>
                        LocalStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
                        {
                            QueueUrl = queueUrl,
                            ReceiptHandle = msg.ReceiptHandle
                        }));
                    
                    await Task.WhenAll(deleteTasks);
                    receivedCount += response.Messages.Count;
                }
                else if (receivedCount > 0)
                {
                    break; // No more messages
                }
                
                attempts++;
            }
            
            return receivedCount;
        });
        
        await Task.WhenAll(receiveTasks);
    }
    
    /// <summary>
    /// Benchmark: SNS publish with subject line
    /// Measures performance impact of including subject in SNS messages
    /// </summary>
    [Benchmark(Description = "SNS Topic - Publish with Subject")]
    public async Task SnsPublishWithSubject()
    {
        if (LocalStack?.SnsClient == null || string.IsNullOrEmpty(_topicArn))
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        
        for (int i = 0; i < MessageCount; i++)
        {
            await LocalStack.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = _topicArn,
                Message = messageBody,
                Subject = $"Test Event {i}",
                MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                {
                    ["MessageIndex"] = new SnsMessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    }
                }
            });
        }
    }
    
    /// <summary>
    /// Benchmark: SNS message deduplication overhead
    /// Measures performance with message deduplication IDs
    /// </summary>
    [Benchmark(Description = "SNS Topic - Message Deduplication")]
    public async Task SnsMessageDeduplication()
    {
        if (LocalStack?.SnsClient == null || string.IsNullOrEmpty(_topicArn))
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        
        for (int i = 0; i < MessageCount; i++)
        {
            await LocalStack.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = _topicArn,
                Message = messageBody,
                MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                {
                    ["MessageDeduplicationId"] = new SnsMessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = $"dedup-{i}-{Guid.NewGuid():N}"
                    },
                    ["MessageIndex"] = new SnsMessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    }
                }
            });
        }
    }
    
    /// <summary>
    /// Helper method to generate message body of specified size
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
}
