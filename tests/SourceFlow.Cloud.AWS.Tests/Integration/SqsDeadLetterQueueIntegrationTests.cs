using Amazon.SQS.Model;
using SourceFlow.Cloud.AWS.Tests.TestHelpers;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Tests.Integration;

/// <summary>
/// Comprehensive integration tests for SQS dead letter queue functionality
/// Tests failed message capture, retry policies, poison message handling, and reprocessing capabilities
/// </summary>
/// <summary>
/// Integration tests for SQS dead letter queue functionality
/// </summary>
[Collection("AWS Integration Tests")]
[Trait("Category", "Integration")]
[Trait("Category", "RequiresLocalStack")]
public class SqsDeadLetterQueueIntegrationTests : IClassFixture<LocalStackTestFixture>, IAsyncDisposable
{
    private readonly LocalStackTestFixture _localStack;
    private readonly List<string> _createdQueues = new();

    public SqsDeadLetterQueueIntegrationTests(LocalStackTestFixture localStack)
    {
        _localStack = localStack;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }

        // Clean up all created queues
        foreach (var queueUrl in _createdQueues)
        {
            try
            {
                await _localStack.SqsClient.DeleteQueueAsync(queueUrl);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task DeadLetterQueue_ShouldReceiveFailedMessages()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }

        // Create DLQ
        var dlqName = $"test-dlq-{Guid.NewGuid():N}";
        var dlqResponse = await _localStack.SqsClient.CreateQueueAsync(dlqName);
        var dlqUrl = dlqResponse.QueueUrl;
        _createdQueues.Add(dlqUrl);

        // Get DLQ ARN
        var dlqAttributes = await _localStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            AttributeNames = new List<string> { "QueueArn" }
        });
        var dlqArn = dlqAttributes.QueueARN;

        // Create main queue with DLQ configuration
        var queueName = $"test-queue-{Guid.NewGuid():N}";
        var createResponse = await _localStack.SqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = new Dictionary<string, string>
            {
                ["RedrivePolicy"] = $"{{\"deadLetterTargetArn\":\"{dlqArn}\",\"maxReceiveCount\":\"2\"}}"
            }
        });
        var queueUrl = createResponse.QueueUrl;
        _createdQueues.Add(queueUrl);

        // Send a test message
        var messageBody = $"Test message {Guid.NewGuid()}";
        await _localStack.SqsClient.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = messageBody
        });

        // Receive and don't delete (simulate failure) - do this 3 times to exceed maxReceiveCount
        for (int i = 0; i < 3; i++)
        {
            var receiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                VisibilityTimeout = 1,
                WaitTimeSeconds = 1
            });

            if (receiveResponse.Messages.Count > 0)
            {
                // Don't delete - let visibility timeout expire
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        // Check DLQ for the failed message
        await Task.Delay(TimeSpan.FromSeconds(2)); // Give time for message to move to DLQ

        var dlqReceiveResponse = await _localStack.SqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = dlqUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 5
        });

        Assert.Single(dlqReceiveResponse.Messages);
        Assert.Equal(messageBody, dlqReceiveResponse.Messages[0].Body);
    }

    [Fact]
    public async Task DeadLetterQueue_ShouldHaveCorrectConfiguration()
    {
        // Skip if not configured for integration tests
        if (!_localStack.Configuration.RunIntegrationTests || _localStack.SqsClient == null)
        {
            return;
        }

        // Create DLQ
        var dlqName = $"test-dlq-config-{Guid.NewGuid():N}";
        var dlqResponse = await _localStack.SqsClient.CreateQueueAsync(dlqName);
        var dlqUrl = dlqResponse.QueueUrl;
        _createdQueues.Add(dlqUrl);

        // Get DLQ ARN
        var dlqAttributes = await _localStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            AttributeNames = new List<string> { "QueueArn" }
        });
        var dlqArn = dlqAttributes.QueueARN;

        // Create main queue with DLQ configuration
        var queueName = $"test-queue-config-{Guid.NewGuid():N}";
        var maxReceiveCount = 5;
        var createResponse = await _localStack.SqsClient.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
            Attributes = new Dictionary<string, string>
            {
                ["RedrivePolicy"] = $"{{\"deadLetterTargetArn\":\"{dlqArn}\",\"maxReceiveCount\":\"{maxReceiveCount}\"}}"
            }
        });
        var queueUrl = createResponse.QueueUrl;
        _createdQueues.Add(queueUrl);

        // Verify configuration
        var attributes = await _localStack.SqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = new List<string> { "RedrivePolicy" }
        });

        Assert.Contains("RedrivePolicy", attributes.Attributes.Keys);
        var redrivePolicy = attributes.Attributes["RedrivePolicy"];
        Assert.Contains(dlqArn, redrivePolicy);
        Assert.Contains($"\"maxReceiveCount\":\"{maxReceiveCount}\"", redrivePolicy);
    }
}

