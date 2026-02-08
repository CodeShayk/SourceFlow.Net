using Amazon.SQS.Model;
using BenchmarkDotNet.Attributes;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;

namespace SourceFlow.Cloud.AWS.Tests.Performance;

/// <summary>
/// Performance benchmarks for SQS operations
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
public class SqsPerformanceBenchmarks : PerformanceBenchmarkBase
{
    private string? _testQueueUrl;
    
    [GlobalSetup]
    public override async Task GlobalSetup()
    {
        await base.GlobalSetup();
        
        if (LocalStack?.SqsClient != null && LocalStack.Configuration.RunPerformanceTests)
        {
            // Create a dedicated queue for performance testing
            var queueName = $"perf-test-queue-{Guid.NewGuid():N}";
            var response = await LocalStack.SqsClient.CreateQueueAsync(queueName);
            _testQueueUrl = response.QueueUrl;
        }
    }
    
    [GlobalCleanup]
    public override async Task GlobalCleanup()
    {
        if (LocalStack?.SqsClient != null && !string.IsNullOrEmpty(_testQueueUrl))
        {
            try
            {
                await LocalStack.SqsClient.DeleteQueueAsync(_testQueueUrl);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        
        await base.GlobalCleanup();
    }
    
    [Benchmark]
    public async Task SendSingleMessage()
    {
        if (LocalStack?.SqsClient == null || string.IsNullOrEmpty(_testQueueUrl))
            return;
        
        var messageBody = $"Benchmark message {Guid.NewGuid()}";
        await LocalStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _testQueueUrl,
            MessageBody = messageBody
        });
    }
    
    [Benchmark]
    public async Task SendMessageWithAttributes()
    {
        if (LocalStack?.SqsClient == null || string.IsNullOrEmpty(_testQueueUrl))
            return;
        
        var messageBody = $"Benchmark message with attributes {Guid.NewGuid()}";
        await LocalStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _testQueueUrl,
            MessageBody = messageBody,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["CommandType"] = new MessageAttributeValue
                {
                    DataType = "String",
                    StringValue = "TestCommand"
                },
                ["EntityId"] = new MessageAttributeValue
                {
                    DataType = "Number",
                    StringValue = "123"
                },
                ["SequenceNo"] = new MessageAttributeValue
                {
                    DataType = "Number", 
                    StringValue = "1"
                }
            }
        });
    }
    
    [Benchmark]
    [Arguments(10)]
    [Arguments(50)]
    [Arguments(100)]
    public async Task SendBatchMessages(int batchSize)
    {
        if (LocalStack?.SqsClient == null || string.IsNullOrEmpty(_testQueueUrl))
            return;
        
        var entries = new List<SendMessageBatchRequestEntry>();
        
        for (int i = 0; i < Math.Min(batchSize, 10); i++) // SQS batch limit is 10
        {
            entries.Add(new SendMessageBatchRequestEntry
            {
                Id = i.ToString(),
                MessageBody = $"Batch message {i} - {Guid.NewGuid()}"
            });
        }
        
        // Send in batches of 10 if batchSize > 10
        for (int i = 0; i < entries.Count; i += 10)
        {
            var batch = entries.Skip(i).Take(10).ToList();
            await LocalStack.SqsClient.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = _testQueueUrl,
                Entries = batch
            });
        }
    }
    
    [Benchmark]
    public async Task ReceiveMessages()
    {
        if (LocalStack?.SqsClient == null || string.IsNullOrEmpty(_testQueueUrl))
            return;
        
        // First send a message to receive
        await LocalStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _testQueueUrl,
            MessageBody = "Message to receive"
        });
        
        // Then receive it
        var response = await LocalStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _testQueueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 1
        });
        
        // Delete received messages
        foreach (var message in response.Messages)
        {
            await LocalStack.SqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = _testQueueUrl,
                ReceiptHandle = message.ReceiptHandle
            });
        }
    }
}