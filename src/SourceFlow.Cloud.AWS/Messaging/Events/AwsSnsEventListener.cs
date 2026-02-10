using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.Core.Configuration;
using SourceFlow.Messaging.Events;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Messaging.Events;

public class AwsSnsEventListener : BackgroundService
{
    private readonly IAmazonSQS _sqsClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly IEventRoutingConfiguration _routingConfig;
    private readonly ILogger<AwsSnsEventListener> _logger;
    private readonly AwsOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public AwsSnsEventListener(
        IAmazonSQS sqsClient,
        IServiceProvider serviceProvider,
        IEventRoutingConfiguration routingConfig,
        ILogger<AwsSnsEventListener> logger,
        AwsOptions options)
    {
        _sqsClient = sqsClient;
        _serviceProvider = serviceProvider;
        _routingConfig = routingConfig;
        _logger = logger;
        _options = options;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Get all SQS queue URLs subscribed to SNS topics
        var queueUrls = _routingConfig.GetListeningQueues();

        if (!queueUrls.Any())
        {
            _logger.LogWarning("No SQS queues configured for SNS listening. AWS event listener will not start.");
            return;
        }

        // Create listening tasks for each queue
        var listeningTasks = queueUrls.Select(queueUrl =>
            ListenToQueue(queueUrl, stoppingToken));

        await Task.WhenAll(listeningTasks);
    }

    private async Task ListenToQueue(string queueUrl, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting to listen to SQS queue for SNS events: {QueueUrl}", queueUrl);
        int retryCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var request = new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = _options.SqsMaxNumberOfMessages,
                    WaitTimeSeconds = _options.SqsReceiveWaitTimeSeconds,
                    MessageAttributeNames = new List<string> { "All" }
                };

                var response = await _sqsClient.ReceiveMessageAsync(request, cancellationToken);

                // Reset retry count on successful receive
                retryCount = 0;

                foreach (var message in response.Messages)
                {
                    await ProcessMessage(message, queueUrl, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listening to SNS/SQS queue: {Queue}, Retry: {RetryCount}", queueUrl, retryCount);

                // Exponential backoff with max delay of 60 seconds
                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retryCount), 60));
                retryCount++;

                await Task.Delay(delay, cancellationToken);
            }
        }

        _logger.LogInformation("Stopped listening to SNS/SQS queue: {QueueUrl}", queueUrl);
    }

    private async Task ProcessMessage(Message message, string queueUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            // 1. Parse SNS notification wrapper
            SnsNotification snsNotification;
            try
            {
                snsNotification = JsonSerializer.Deserialize<SnsNotification>(message.Body, _jsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse SNS notification from message body: {MessageId}", message.MessageId);
                // Try to delete the message to prevent infinite retries if it's malformed
                await _sqsClient.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle
                }, cancellationToken);
                return;
            }

            // 2. Get event type from message attributes
            var eventTypeName = snsNotification.MessageAttributes?.GetValueOrDefault("EventType")?.Value;
            if (string.IsNullOrEmpty(eventTypeName))
            {
                _logger.LogError("SNS message missing EventType attribute: {MessageId}", message.MessageId);
                return;
            }

            var eventType = Type.GetType(eventTypeName);
            if (eventType == null)
            {
                _logger.LogError("Could not resolve event type: {EventType}", eventTypeName);
                return;
            }

            // 3. Deserialize event from SNS message body
            var @event = JsonSerializer.Deserialize(snsNotification.Message, eventType, _jsonOptions) as IEvent;
            if (@event == null)
            {
                _logger.LogError("Failed to deserialize event: {EventType}", eventTypeName);
                return;
            }

            // 4. Get event subscribers (singleton, so no scope needed for this part)
            using var scope = _serviceProvider.CreateScope();
            var eventSubscribers = scope.ServiceProvider.GetServices<IEventSubscriber>();

            // 5. Invoke Subscribe method for each subscriber
            var subscribeMethod = typeof(IEventSubscriber)
                .GetMethod("Subscribe")
                ?.MakeGenericMethod(eventType);

            if (subscribeMethod == null)
            {
                _logger.LogError("Could not find Subscribe method for event type: {EventType}", eventTypeName);
                return;
            }

            var tasks = eventSubscribers.Select(subscriber =>
            {
                try
                {
                    return (Task)subscribeMethod.Invoke(subscriber, new[] { @event });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error invoking Subscribe method for event type: {EventType}", eventTypeName);
                    return Task.CompletedTask;
                }
            });

            await Task.WhenAll(tasks);

            // 6. Delete message from queue
            await _sqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = queueUrl,
                ReceiptHandle = message.ReceiptHandle
            }, cancellationToken);

            _logger.LogInformation("Event processed from SNS: {EventType} (MessageId: {MessageId})",
                eventType.Name, message.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing SNS message: {MessageId}", message.MessageId);
        }
    }

    // SNS notification wrapper structure
    private class SnsNotification
    {
        public string Type { get; set; }
        public string MessageId { get; set; }
        public string TopicArn { get; set; }
        public string Subject { get; set; }
        public string Message { get; set; }
        public Dictionary<string, SnsMessageAttribute> MessageAttributes { get; set; }
    }

    private class SnsMessageAttribute
    {
        public string Type { get; set; }
        public string Value { get; set; }
    }
}

// Extension method to safely get dictionary values
file static class DictionaryExtensions
{
    public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key)
    {
        return dictionary.TryGetValue(key, out var value) ? value : default(TValue);
    }
}