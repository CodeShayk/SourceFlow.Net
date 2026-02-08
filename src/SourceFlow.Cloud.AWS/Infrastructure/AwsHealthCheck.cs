using Amazon.SQS;
using Amazon.SimpleNotificationService;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SourceFlow.Cloud.AWS.Configuration;

namespace SourceFlow.Cloud.AWS.Infrastructure;

public class AwsHealthCheck : IHealthCheck
{
    private readonly IAmazonSQS _sqsClient;
    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly IAwsCommandRoutingConfiguration _commandRoutingConfig;
    private readonly IAwsEventRoutingConfiguration _eventRoutingConfig;

    public AwsHealthCheck(
        IAmazonSQS sqsClient,
        IAmazonSimpleNotificationService snsClient,
        IAwsCommandRoutingConfiguration commandRoutingConfig,
        IAwsEventRoutingConfiguration eventRoutingConfig)
    {
        _sqsClient = sqsClient;
        _snsClient = snsClient;
        _commandRoutingConfig = commandRoutingConfig;
        _eventRoutingConfig = eventRoutingConfig;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Test SQS connectivity by listing queues (or trying to access configured queues)
            var commandQueues = _commandRoutingConfig.GetListeningQueues().Take(1).ToList();
            if (commandQueues.Any())
            {
                // Try to get attributes of first queue to test connectivity
                var queueUrl = commandQueues.First();
                await _sqsClient.GetQueueAttributesAsync(queueUrl, new List<string> { "QueueArn" }, cancellationToken);
            }

            // Test SNS connectivity by trying to list topics (or verify configured topics)
            var eventQueues = _eventRoutingConfig.GetListeningQueues().Take(1).ToList();
            if (eventQueues.Any())
            {
                // Just verify we can make a call to SNS service
                await _snsClient.ListTopicsAsync(cancellationToken);
            }

            return HealthCheckResult.Healthy("AWS services are accessible");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"AWS services are not accessible: {ex.Message}", ex);
        }
    }
}