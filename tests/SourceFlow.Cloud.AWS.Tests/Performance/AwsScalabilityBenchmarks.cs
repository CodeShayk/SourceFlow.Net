using System.Diagnostics;
using System.Text;
using Amazon.SQS.Model;
using Amazon.SimpleNotificationService.Model;
using BenchmarkDotNet.Attributes;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace SourceFlow.Cloud.AWS.Tests.Performance;

/// <summary>
/// Comprehensive scalability benchmarks for AWS services
/// Validates Requirements 5.4, 5.5 - Resource utilization and scalability testing
/// 
/// This benchmark suite provides comprehensive scalability testing for:
/// - Performance under increasing concurrent connections
/// - Resource utilization (memory, CPU, network) under load
/// - Performance scaling characteristics
/// - AWS service limit impact on performance
/// - Combined SQS and SNS scalability scenarios
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 3)]
public class AwsScalabilityBenchmarks : PerformanceBenchmarkBase
{
    private readonly List<string> _standardQueueUrls = new();
    private readonly List<string> _fifoQueueUrls = new();
    private readonly List<string> _topicArns = new();
    private readonly List<string> _subscriberQueueUrls = new();
    
    // Scalability test parameters
    [Params(1, 5, 10, 20)]
    public int ConcurrentConnections { get; set; }
    
    [Params(100, 500, 1000)]
    public int MessagesPerConnection { get; set; }
    
    [Params(256, 1024)]
    public int MessageSizeBytes { get; set; }
    
    [Params(1, 3, 5)]
    public int ResourceCount { get; set; }
    
    [GlobalSetup]
    public override async Task GlobalSetup()
    {
        await base.GlobalSetup();
        
        if (LocalStack?.SqsClient != null && LocalStack?.SnsClient != null && LocalStack.Configuration.RunPerformanceTests)
        {
            // Create multiple standard queues for scalability testing
            for (int i = 0; i < ResourceCount; i++)
            {
                var standardQueueName = $"scale-test-standard-{i}-{Guid.NewGuid():N}";
                var standardResponse = await LocalStack.SqsClient.CreateQueueAsync(new CreateQueueRequest
                {
                    QueueName = standardQueueName,
                    Attributes = new Dictionary<string, string>
                    {
                        ["MessageRetentionPeriod"] = "3600",
                        ["VisibilityTimeout"] = "30"
                    }
                });
                _standardQueueUrls.Add(standardResponse.QueueUrl);
                
                // Create FIFO queues
                var fifoQueueName = $"scale-test-fifo-{i}-{Guid.NewGuid():N}.fifo";
                var fifoResponse = await LocalStack.SqsClient.CreateQueueAsync(new CreateQueueRequest
                {
                    QueueName = fifoQueueName,
                    Attributes = new Dictionary<string, string>
                    {
                        ["FifoQueue"] = "true",
                        ["ContentBasedDeduplication"] = "true",
                        ["MessageRetentionPeriod"] = "3600",
                        ["VisibilityTimeout"] = "30"
                    }
                });
                _fifoQueueUrls.Add(fifoResponse.QueueUrl);
                
                // Create SNS topics
                var topicName = $"scale-test-topic-{i}-{Guid.NewGuid():N}";
                var topicResponse = await LocalStack.SnsClient.CreateTopicAsync(new CreateTopicRequest
                {
                    Name = topicName
                });
                _topicArns.Add(topicResponse.TopicArn);
                
                // Create subscriber queues for each topic
                var subscriberQueueName = $"scale-test-subscriber-{i}-{Guid.NewGuid():N}";
                var subscriberResponse = await LocalStack.SqsClient.CreateQueueAsync(new CreateQueueRequest
                {
                    QueueName = subscriberQueueName,
                    Attributes = new Dictionary<string, string>
                    {
                        ["MessageRetentionPeriod"] = "3600",
                        ["VisibilityTimeout"] = "30"
                    }
                });
                _subscriberQueueUrls.Add(subscriberResponse.QueueUrl);
                
                // Subscribe queue to topic
                var queueAttributes = await LocalStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
                {
                    QueueUrl = subscriberResponse.QueueUrl,
                    AttributeNames = new List<string> { "QueueArn" }
                });
                var queueArn = queueAttributes.Attributes["QueueArn"];
                
                await LocalStack.SnsClient.SubscribeAsync(new SubscribeRequest
                {
                    TopicArn = topicResponse.TopicArn,
                    Protocol = "sqs",
                    Endpoint = queueArn
                });
            }
        }
    }
    
    [GlobalCleanup]
    public override async Task GlobalCleanup()
    {
        if (LocalStack?.SqsClient != null && LocalStack?.SnsClient != null)
        {
            // Clean up all queues
            foreach (var queueUrl in _standardQueueUrls.Concat(_fifoQueueUrls).Concat(_subscriberQueueUrls))
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
            
            // Clean up all topics
            foreach (var topicArn in _topicArns)
            {
                try
                {
                    await LocalStack.SnsClient.DeleteTopicAsync(new DeleteTopicRequest
                    {
                        TopicArn = topicArn
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
    /// Benchmark: SQS scalability with increasing concurrent connections
    /// Measures throughput and resource utilization as connections increase
    /// </summary>
    [Benchmark(Description = "SQS Scalability - Increasing Concurrent Connections")]
    public async Task SqsScalabilityWithConcurrentConnections()
    {
        if (LocalStack?.SqsClient == null || _standardQueueUrls.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        var queueUrl = _standardQueueUrls[0];
        
        // Create concurrent tasks that send messages
        var tasks = Enumerable.Range(0, ConcurrentConnections)
            .Select(async connectionId =>
            {
                for (int i = 0; i < MessagesPerConnection; i++)
                {
                    await LocalStack.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MessageBody = messageBody,
                        MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                        {
                            ["ConnectionId"] = new SqsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = connectionId.ToString()
                            },
                            ["MessageIndex"] = new SqsMessageAttributeValue
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
    /// Benchmark: SNS scalability with increasing concurrent connections
    /// Measures publish throughput and fan-out performance as connections increase
    /// </summary>
    [Benchmark(Description = "SNS Scalability - Increasing Concurrent Connections")]
    public async Task SnsScalabilityWithConcurrentConnections()
    {
        if (LocalStack?.SnsClient == null || _topicArns.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        var topicArn = _topicArns[0];
        
        // Create concurrent tasks that publish messages
        var tasks = Enumerable.Range(0, ConcurrentConnections)
            .Select(async connectionId =>
            {
                for (int i = 0; i < MessagesPerConnection; i++)
                {
                    await LocalStack.SnsClient.PublishAsync(new PublishRequest
                    {
                        TopicArn = topicArn,
                        Message = messageBody,
                        MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                        {
                            ["ConnectionId"] = new SnsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = connectionId.ToString()
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
    /// Benchmark: Multi-queue scalability with load distribution
    /// Measures performance when distributing load across multiple queues
    /// </summary>
    [Benchmark(Description = "SQS Multi-Queue - Load Distribution Scalability")]
    public async Task SqsMultiQueueLoadDistribution()
    {
        if (LocalStack?.SqsClient == null || _standardQueueUrls.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        
        // Distribute connections across available queues
        var tasks = Enumerable.Range(0, ConcurrentConnections)
            .Select(async connectionId =>
            {
                var queueUrl = _standardQueueUrls[connectionId % _standardQueueUrls.Count];
                
                for (int i = 0; i < MessagesPerConnection; i++)
                {
                    await LocalStack.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MessageBody = messageBody,
                        MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                        {
                            ["ConnectionId"] = new SqsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = connectionId.ToString()
                            },
                            ["QueueIndex"] = new SqsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = (connectionId % _standardQueueUrls.Count).ToString()
                            }
                        }
                    });
                }
            });
        
        await Task.WhenAll(tasks);
    }
    
    /// <summary>
    /// Benchmark: Multi-topic scalability with load distribution
    /// Measures performance when distributing load across multiple topics
    /// </summary>
    [Benchmark(Description = "SNS Multi-Topic - Load Distribution Scalability")]
    public async Task SnsMultiTopicLoadDistribution()
    {
        if (LocalStack?.SnsClient == null || _topicArns.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        
        // Distribute connections across available topics
        var tasks = Enumerable.Range(0, ConcurrentConnections)
            .Select(async connectionId =>
            {
                var topicArn = _topicArns[connectionId % _topicArns.Count];
                
                for (int i = 0; i < MessagesPerConnection; i++)
                {
                    await LocalStack.SnsClient.PublishAsync(new PublishRequest
                    {
                        TopicArn = topicArn,
                        Message = messageBody,
                        MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                        {
                            ["ConnectionId"] = new SnsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = connectionId.ToString()
                            },
                            ["TopicIndex"] = new SnsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = (connectionId % _topicArns.Count).ToString()
                            }
                        }
                    });
                }
            });
        
        await Task.WhenAll(tasks);
    }
    
    /// <summary>
    /// Benchmark: FIFO queue scalability with multiple message groups
    /// Measures FIFO performance with parallel message groups
    /// </summary>
    [Benchmark(Description = "FIFO Queue - Message Group Scalability")]
    public async Task FifoQueueMessageGroupScalability()
    {
        if (LocalStack?.SqsClient == null || _fifoQueueUrls.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        var queueUrl = _fifoQueueUrls[0];
        
        // Each connection uses its own message group for parallel processing
        var tasks = Enumerable.Range(0, ConcurrentConnections)
            .Select(async connectionId =>
            {
                var messageGroupId = $"group-{connectionId}";
                
                for (int i = 0; i < MessagesPerConnection; i++)
                {
                    await LocalStack.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MessageBody = messageBody,
                        MessageGroupId = messageGroupId,
                        MessageDeduplicationId = $"conn-{connectionId}-msg-{i}-{Guid.NewGuid():N}",
                        MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                        {
                            ["ConnectionId"] = new SqsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = connectionId.ToString()
                            },
                            ["MessageGroupId"] = new SqsMessageAttributeValue
                            {
                                DataType = "String",
                                StringValue = messageGroupId
                            }
                        }
                    });
                }
            });
        
        await Task.WhenAll(tasks);
    }
    
    /// <summary>
    /// Benchmark: Combined SQS and SNS scalability
    /// Measures end-to-end scalability with SNS publishing and SQS consumption
    /// </summary>
    [Benchmark(Description = "Combined SQS+SNS - End-to-End Scalability")]
    public async Task CombinedSqsSnsScalability()
    {
        if (LocalStack?.SnsClient == null || LocalStack?.SqsClient == null || 
            _topicArns.Count == 0 || _subscriberQueueUrls.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        var messagesPerConnection = Math.Min(MessagesPerConnection, 50); // Limit for combined test
        
        // Publish messages concurrently to topics
        var publishTasks = Enumerable.Range(0, ConcurrentConnections)
            .Select(async connectionId =>
            {
                var topicArn = _topicArns[connectionId % _topicArns.Count];
                
                for (int i = 0; i < messagesPerConnection; i++)
                {
                    await LocalStack.SnsClient.PublishAsync(new PublishRequest
                    {
                        TopicArn = topicArn,
                        Message = messageBody,
                        MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                        {
                            ["ConnectionId"] = new SnsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = connectionId.ToString()
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
        await Task.Delay(1000);
        
        // Receive messages concurrently from subscriber queues
        var receiveTasks = _subscriberQueueUrls.Select(async queueUrl =>
        {
            var receivedCount = 0;
            var maxAttempts = 10;
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
                    break;
                }
                
                attempts++;
            }
            
            return receivedCount;
        });
        
        await Task.WhenAll(receiveTasks);
    }
    
    /// <summary>
    /// Benchmark: Batch operations scalability
    /// Measures scalability of batch send operations with concurrent connections
    /// </summary>
    [Benchmark(Description = "SQS Batch - Concurrent Batch Operations Scalability")]
    public async Task SqsBatchOperationsScalability()
    {
        if (LocalStack?.SqsClient == null || _standardQueueUrls.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        var queueUrl = _standardQueueUrls[0];
        var batchSize = 10; // AWS SQS batch limit
        var batchesPerConnection = MessagesPerConnection / batchSize;
        
        // Create concurrent tasks that send batches
        var tasks = Enumerable.Range(0, ConcurrentConnections)
            .Select(async connectionId =>
            {
                for (int batch = 0; batch < batchesPerConnection; batch++)
                {
                    var entries = new List<SendMessageBatchRequestEntry>();
                    
                    for (int i = 0; i < batchSize; i++)
                    {
                        entries.Add(new SendMessageBatchRequestEntry
                        {
                            Id = i.ToString(),
                            MessageBody = messageBody,
                            MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                            {
                                ["ConnectionId"] = new SqsMessageAttributeValue
                                {
                                    DataType = "Number",
                                    StringValue = connectionId.ToString()
                                },
                                ["BatchIndex"] = new SqsMessageAttributeValue
                                {
                                    DataType = "Number",
                                    StringValue = batch.ToString()
                                }
                            }
                        });
                    }
                    
                    await LocalStack.SqsClient.SendMessageBatchAsync(new SendMessageBatchRequest
                    {
                        QueueUrl = queueUrl,
                        Entries = entries
                    });
                }
            });
        
        await Task.WhenAll(tasks);
    }
    
    /// <summary>
    /// Benchmark: Concurrent receive operations scalability
    /// Measures scalability of message consumption with multiple concurrent receivers
    /// </summary>
    [Benchmark(Description = "SQS Receive - Concurrent Receivers Scalability")]
    public async Task SqsConcurrentReceiversScalability()
    {
        if (LocalStack?.SqsClient == null || _standardQueueUrls.Count == 0)
            return;
        
        var queueUrl = _standardQueueUrls[0];
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        var totalMessages = ConcurrentConnections * MessagesPerConnection;
        
        // First, populate the queue with messages
        var populateTasks = Enumerable.Range(0, totalMessages)
            .Select(i => LocalStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = messageBody,
                MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                {
                    ["MessageIndex"] = new SqsMessageAttributeValue
                    {
                        DataType = "Number",
                        StringValue = i.ToString()
                    }
                }
            }));
        
        await Task.WhenAll(populateTasks);
        
        // Now receive messages concurrently
        var messagesPerReceiver = totalMessages / ConcurrentConnections;
        var receiveTasks = Enumerable.Range(0, ConcurrentConnections)
            .Select(async receiverId =>
            {
                var receivedCount = 0;
                
                while (receivedCount < messagesPerReceiver)
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
                    else
                    {
                        break; // No more messages available
                    }
                }
                
                return receivedCount;
            });
        
        await Task.WhenAll(receiveTasks);
    }
    
    /// <summary>
    /// Benchmark: Message size impact on scalability
    /// Measures how message size affects throughput with concurrent connections
    /// </summary>
    [Benchmark(Description = "SQS Scalability - Message Size Impact")]
    public async Task SqsMessageSizeScalabilityImpact()
    {
        if (LocalStack?.SqsClient == null || _standardQueueUrls.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        var queueUrl = _standardQueueUrls[0];
        
        // Test with varying message sizes and concurrent connections
        var tasks = Enumerable.Range(0, ConcurrentConnections)
            .Select(async connectionId =>
            {
                for (int i = 0; i < MessagesPerConnection; i++)
                {
                    await LocalStack.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MessageBody = messageBody,
                        MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                        {
                            ["ConnectionId"] = new SqsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = connectionId.ToString()
                            },
                            ["MessageSize"] = new SqsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = MessageSizeBytes.ToString()
                            }
                        }
                    });
                }
            });
        
        await Task.WhenAll(tasks);
    }
    
    /// <summary>
    /// Benchmark: Resource count impact on scalability
    /// Measures how the number of queues/topics affects overall throughput
    /// </summary>
    [Benchmark(Description = "Multi-Resource - Resource Count Scalability Impact")]
    public async Task MultiResourceScalabilityImpact()
    {
        if (LocalStack?.SqsClient == null || _standardQueueUrls.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        
        // Distribute connections evenly across all available queues
        var tasks = Enumerable.Range(0, ConcurrentConnections)
            .Select(async connectionId =>
            {
                var queueIndex = connectionId % _standardQueueUrls.Count;
                var queueUrl = _standardQueueUrls[queueIndex];
                
                for (int i = 0; i < MessagesPerConnection; i++)
                {
                    await LocalStack.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MessageBody = messageBody,
                        MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                        {
                            ["ConnectionId"] = new SqsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = connectionId.ToString()
                            },
                            ["QueueIndex"] = new SqsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = queueIndex.ToString()
                            },
                            ["ResourceCount"] = new SqsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = _standardQueueUrls.Count.ToString()
                            }
                        }
                    });
                }
            });
        
        await Task.WhenAll(tasks);
    }
    
    /// <summary>
    /// Benchmark: Mixed workload scalability
    /// Measures performance with mixed send/receive operations
    /// </summary>
    [Benchmark(Description = "SQS Mixed - Send and Receive Scalability")]
    public async Task SqsMixedWorkloadScalability()
    {
        if (LocalStack?.SqsClient == null || _standardQueueUrls.Count == 0)
            return;
        
        var messageBody = GenerateMessageBody(MessageSizeBytes);
        var queueUrl = _standardQueueUrls[0];
        var halfConnections = ConcurrentConnections / 2;
        
        // Half connections send messages
        var sendTasks = Enumerable.Range(0, halfConnections)
            .Select(async connectionId =>
            {
                for (int i = 0; i < MessagesPerConnection; i++)
                {
                    await LocalStack.SqsClient.SendMessageAsync(new SendMessageRequest
                    {
                        QueueUrl = queueUrl,
                        MessageBody = messageBody,
                        MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                        {
                            ["ConnectionId"] = new SqsMessageAttributeValue
                            {
                                DataType = "Number",
                                StringValue = connectionId.ToString()
                            },
                            ["OperationType"] = new SqsMessageAttributeValue
                            {
                                DataType = "String",
                                StringValue = "Send"
                            }
                        }
                    });
                }
            });
        
        // Half connections receive messages
        var receiveTasks = Enumerable.Range(halfConnections, halfConnections)
            .Select(async connectionId =>
            {
                var receivedCount = 0;
                var targetCount = MessagesPerConnection;
                
                while (receivedCount < targetCount)
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
                    else
                    {
                        // Wait a bit for more messages
                        await Task.Delay(100);
                    }
                }
                
                return receivedCount;
            });
        
        // Run send and receive operations concurrently
        await Task.WhenAll(sendTasks.Concat(receiveTasks));
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
