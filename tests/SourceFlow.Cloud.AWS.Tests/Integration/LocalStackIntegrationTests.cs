using Amazon.SQS.Model;
using Amazon.SimpleNotificationService.Model;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Integration tests using LocalStack emulator
/// </summary>
public class LocalStackIntegrationTests : IClassFixture<LocalStackTestFixture>
{
    private readonly LocalStackTestFixture _localStack;
    
    public LocalStackIntegrationTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
    }
    
    [Fact]
    public async Task LocalStack_ShouldBeAvailable()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests)
        {
            return;
        }
        
        // Verify LocalStack is running and accessible
        var isAvailable = await _localStack.IsAvailableAsync();
        Assert.True(isAvailable, "LocalStack should be available for integration tests");
    }
    
    [Fact]
    public async Task SQS_ShouldCreateAndListQueues()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Create a test queue
        var queueName = $"test-queue-{Guid.NewGuid():N}";
        var createResponse = await _localStack.SqsClient.CreateQueueAsync(queueName);
        
        Assert.NotNull(createResponse.QueueUrl);
        Assert.Contains(queueName, createResponse.QueueUrl);
        
        // List queues and verify our queue exists
        var listResponse = await _localStack.SqsClient.ListQueuesAsync(new ListQueuesRequest());
        Assert.Contains(createResponse.QueueUrl, listResponse.QueueUrls);
        
        // Clean up
        await _localStack.SqsClient.DeleteQueueAsync(createResponse.QueueUrl);
    }
    
    [Fact]
    public async Task SNS_ShouldCreateAndListTopics()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SnsClient == null)
        {
            return;
        }
        
        // Create a test topic
        var topicName = $"test-topic-{Guid.NewGuid():N}";
        var createResponse = await _localStack.SnsClient.CreateTopicAsync(topicName);
        
        Assert.NotNull(createResponse.TopicArn);
        Assert.Contains(topicName, createResponse.TopicArn);
        
        // List topics and verify our topic exists
        var listResponse = await _localStack.SnsClient.ListTopicsAsync();
        Assert.Contains(createResponse.TopicArn, listResponse.Topics.Select(t => t.TopicArn));
        
        // Clean up
        await _localStack.SnsClient.DeleteTopicAsync(createResponse.TopicArn);
    }
    
    [Fact]
    public async Task SQS_ShouldSendAndReceiveMessages()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }
        
        // Create a test queue
        var queueName = $"test-message-queue-{Guid.NewGuid():N}";
        var createResponse = await _localStack.SqsClient.CreateQueueAsync(queueName);
        var queueUrl = createResponse.QueueUrl;
        
        try
        {
            // Send a test message
            var messageBody = $"Test message {Guid.NewGuid()}";
            var sendResponse = await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = queueUrl,
                MessageBody = messageBody,
                MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
                {
                    ["TestAttribute"] = new SqsMessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "TestValue"
                    }
                }
            });
            
            Assert.NotNull(sendResponse.MessageId);
            
            // Receive the message
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                MessageAttributeNames = new List<string> { "All" },
                WaitTimeSeconds = 1
            });
            
            Assert.Single(receiveResponse.Messages);
            var receivedMessage = receiveResponse.Messages[0];
            
            Assert.Equal(messageBody, receivedMessage.Body);
            Assert.Contains("TestAttribute", receivedMessage.MessageAttributes.Keys);
            Assert.Equal("TestValue", receivedMessage.MessageAttributes["TestAttribute"].StringValue);
        }
        finally
        {
            // Clean up
            await _localStack.SqsClient.DeleteQueueAsync(queueUrl);
        }
    }
    
    [Fact]
    public async Task SNS_ShouldPublishMessages()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SnsClient == null)
        {
            return;
        }
        
        // Create a test topic
        var topicName = $"test-publish-topic-{Guid.NewGuid():N}";
        var createResponse = await _localStack.SnsClient.CreateTopicAsync(topicName);
        var topicArn = createResponse.TopicArn;
        
        try
        {
            // Publish a test message
            var messageBody = $"Test SNS message {Guid.NewGuid()}";
            var publishResponse = await _localStack.SnsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = messageBody,
                Subject = "Test Subject",
                MessageAttributes = new Dictionary<string, SnsMessageAttributeValue>
                {
                    ["TestAttribute"] = new SnsMessageAttributeValue
                    {
                        DataType = "String",
                        StringValue = "TestValue"
                    }
                }
            });
            
            Assert.NotNull(publishResponse.MessageId);
        }
        finally
        {
            // Clean up
            await _localStack.SnsClient.DeleteTopicAsync(topicArn);
        }
    }
}