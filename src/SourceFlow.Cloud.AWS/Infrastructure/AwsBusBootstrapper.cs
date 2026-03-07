using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Configuration;

namespace SourceFlow.Cloud.AWS.Infrastructure;

/// <summary>
/// Hosted service that runs once at application startup to ensure all configured SQS queues
/// and SNS topics exist in AWS, then resolves short names to full URLs/ARNs and injects them
/// into <see cref="IBusBootstrapConfiguration"/> via <c>Resolve()</c>.
/// </summary>
/// <remarks>
/// Must be registered as a hosted service <b>before</b> <c>AwsSqsCommandListener</c> and
/// <c>AwsSnsEventListener</c> so that routing is fully resolved before any polling begins.
/// </remarks>
public sealed class AwsBusBootstrapper : IHostedService
{
    private readonly IBusBootstrapConfiguration _busConfiguration;
    private readonly IAmazonSQS _sqsClient;
    private readonly IAmazonSimpleNotificationService _snsClient;
    private readonly ILogger<AwsBusBootstrapper> _logger;

    public AwsBusBootstrapper(
        IBusBootstrapConfiguration busConfiguration,
        IAmazonSQS sqsClient,
        IAmazonSimpleNotificationService snsClient,
        ILogger<AwsBusBootstrapper> logger)
    {
        _busConfiguration = busConfiguration;
        _sqsClient = sqsClient;
        _snsClient = snsClient;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AwsBusBootstrapper: resolving SQS queues and SNS topics.");

        // ── 0. Validate: subscribing to topics requires at least one command queue ──

        if (_busConfiguration.SubscribedTopicNames.Count > 0 &&
            _busConfiguration.CommandListeningQueueNames.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one command queue must be configured via .Listen.To.CommandQueue(...) " +
                "when subscribing to topics via .Subscribe.To.Topic(...). " +
                "SNS topic subscriptions require an SQS queue to receive events.");
        }

        // ── 1. Collect all unique queue names ────────────────────────────────

        var allQueueNames = _busConfiguration.CommandTypeToQueueName.Values
            .Concat(_busConfiguration.CommandListeningQueueNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ── 2. Resolve (or create) every queue  ──────────────────────────────

        var queueUrlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var queueName in allQueueNames)
        {
            var url = await GetOrCreateQueueAsync(queueName, cancellationToken);
            queueUrlMap[queueName] = url;
            _logger.LogDebug("AwsBusBootstrapper: queue '{QueueName}' → {Url}", queueName, url);
        }

        // ── 3. Collect all unique topic names ────────────────────────────────

        var allTopicNames = _busConfiguration.EventTypeToTopicName.Values
            .Concat(_busConfiguration.SubscribedTopicNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ── 4. Resolve (or create) every topic ───────────────────────────────

        var topicArnMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var topicName in allTopicNames)
        {
            var arn = await GetOrCreateTopicAsync(topicName, cancellationToken);
            topicArnMap[topicName] = arn;
            _logger.LogDebug("AwsBusBootstrapper: topic '{TopicName}' → {Arn}", topicName, arn);
        }

        // ── 5. Build resolved dictionaries ───────────────────────────────────

        var resolvedCommandRoutes = _busConfiguration.CommandTypeToQueueName
            .ToDictionary(kv => kv.Key, kv => queueUrlMap[kv.Value]);

        var resolvedEventRoutes = _busConfiguration.EventTypeToTopicName
            .ToDictionary(kv => kv.Key, kv => topicArnMap[kv.Value]);

        var resolvedCommandListeningUrls = _busConfiguration.CommandListeningQueueNames
            .Select(name => queueUrlMap[name])
            .ToList();

        var resolvedSubscribedTopicArns = _busConfiguration.SubscribedTopicNames
            .Select(name => topicArnMap[name])
            .ToList();

        // ── 6. Subscribe topics to the first command queue ─────────────────

        var eventListeningUrls = new List<string>();

        if (resolvedSubscribedTopicArns.Count > 0)
        {
            var targetQueueUrl = resolvedCommandListeningUrls[0];
            var targetQueueArn = await GetQueueArnAsync(targetQueueUrl, cancellationToken);

            foreach (var topicArn in resolvedSubscribedTopicArns)
            {
                await SubscribeQueueToTopicAsync(topicArn, targetQueueArn, cancellationToken);
                _logger.LogInformation(
                    "AwsBusBootstrapper: subscribed queue '{QueueArn}' to topic '{TopicArn}'.",
                    targetQueueArn, topicArn);
            }

            eventListeningUrls.Add(targetQueueUrl);
        }

        // ── 7. Inject resolved paths into configuration ───────────────────────

        _busConfiguration.Resolve(
            resolvedCommandRoutes,
            resolvedEventRoutes,
            resolvedCommandListeningUrls,
            resolvedSubscribedTopicArns,
            eventListeningUrls);

        _logger.LogInformation(
            "AwsBusBootstrapper: resolved {CommandCount} command route(s), " +
            "{EventCount} event route(s), {ListenCount} listening queue(s), " +
            "{SubscribeCount} subscribed topic(s).",
            resolvedCommandRoutes.Count,
            resolvedEventRoutes.Count,
            resolvedCommandListeningUrls.Count,
            resolvedSubscribedTopicArns.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> GetOrCreateQueueAsync(string queueName, CancellationToken ct)
    {
        try
        {
            var response = await _sqsClient.GetQueueUrlAsync(queueName, ct);
            return response.QueueUrl;
        }
        catch (QueueDoesNotExistException)
        {
            _logger.LogInformation("AwsBusBootstrapper: queue '{QueueName}' not found — creating.", queueName);

            var request = new CreateQueueRequest { QueueName = queueName };

            if (queueName.EndsWith(".fifo", StringComparison.OrdinalIgnoreCase))
            {
                request.Attributes = new Dictionary<string, string>
                {
                    [QueueAttributeName.FifoQueue] = "true",
                    [QueueAttributeName.ContentBasedDeduplication] = "true"
                };
            }

            var created = await _sqsClient.CreateQueueAsync(request, ct);
            return created.QueueUrl;
        }
    }

    private async Task<string> GetOrCreateTopicAsync(string topicName, CancellationToken ct)
    {
        // CreateTopicAsync is idempotent: returns the existing ARN when the topic already exists.
        var response = await _snsClient.CreateTopicAsync(topicName, ct);
        return response.TopicArn;
    }

    private async Task<string> GetQueueArnAsync(string queueUrl, CancellationToken ct)
    {
        var response = await _sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = new List<string> { QueueAttributeName.QueueArn }
        }, ct);

        return response.Attributes[QueueAttributeName.QueueArn];
    }

    private async Task SubscribeQueueToTopicAsync(string topicArn, string queueArn, CancellationToken ct)
    {
        // SubscribeAsync is idempotent: returns the existing subscription ARN if already subscribed.
        await _snsClient.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn
        }, ct);
    }
}
