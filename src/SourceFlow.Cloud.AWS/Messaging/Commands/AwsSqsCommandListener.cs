using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Messaging.Commands;
using System.Text.Json;

namespace SourceFlow.Cloud.AWS.Messaging.Commands;

public class AwsSqsCommandListener : BackgroundService
{
    private readonly IAmazonSQS _sqsClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAwsCommandRoutingConfiguration _routingConfig;
    private readonly ILogger<AwsSqsCommandListener> _logger;
    private readonly AwsOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public AwsSqsCommandListener(
        IAmazonSQS sqsClient,
        IServiceProvider serviceProvider,
        IAwsCommandRoutingConfiguration routingConfig,
        ILogger<AwsSqsCommandListener> logger,
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
        // Get all queue URLs to listen to
        var queueUrls = _routingConfig.GetListeningQueues();

        if (!queueUrls.Any())
        {
            _logger.LogWarning("No SQS queues configured for listening. AWS command listener will not start.");
            return;
        }

        // Create listening tasks for each queue
        var listeningTasks = queueUrls.Select(queueUrl =>
            ListenToQueue(queueUrl, stoppingToken));

        await Task.WhenAll(listeningTasks);
    }

    private async Task ListenToQueue(string queueUrl, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting to listen to SQS queue: {QueueUrl}", queueUrl);
        int retryCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // 1. Long-poll SQS (up to 20 seconds)
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

                // 2. Process each message
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
                _logger.LogError(ex, "Error listening to SQS queue: {Queue}, Retry: {RetryCount}", queueUrl, retryCount);

                // Exponential backoff with max delay of 60 seconds
                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retryCount), 60));
                retryCount++;

                await Task.Delay(delay, cancellationToken);
            }
        }

        _logger.LogInformation("Stopped listening to SQS queue: {QueueUrl}", queueUrl);
    }

    private async Task ProcessMessage(Message message, string queueUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            // 1. Get command type from message attributes
            if (!message.MessageAttributes.TryGetValue("CommandType", out var commandTypeAttribute))
            {
                _logger.LogError("Message missing CommandType attribute: {MessageId}", message.MessageId);
                return;
            }

            var commandTypeName = commandTypeAttribute.StringValue;
            var commandType = Type.GetType(commandTypeName);

            if (commandType == null)
            {
                _logger.LogError("Could not resolve command type: {CommandType}", commandTypeName);
                return;
            }

            // 2. Deserialize command
            var command = JsonSerializer.Deserialize(message.Body, commandType, _jsonOptions) as ICommand;

            if (command == null)
            {
                _logger.LogError("Failed to deserialize command: {CommandType}", commandTypeName);
                return;
            }

            // 3. Create scoped service provider for command handling
            using var scope = _serviceProvider.CreateScope();
            var commandSubscriber = scope.ServiceProvider
                .GetRequiredService<ICommandSubscriber>();

            // 4. Invoke Subscribe method using reflection (to preserve generics)
            var subscribeMethod = typeof(ICommandSubscriber)
                .GetMethod("Subscribe")
                ?.MakeGenericMethod(commandType);

            if (subscribeMethod == null)
            {
                _logger.LogError("Could not find Subscribe method for command type: {CommandType}", commandTypeName);
                return;
            }

            await (Task)subscribeMethod.Invoke(commandSubscriber, new[] { command });

            // 5. Delete message from queue (successful processing)
            await _sqsClient.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = queueUrl,
                ReceiptHandle = message.ReceiptHandle
            }, cancellationToken);

            _logger.LogInformation("Command processed from SQS: {CommandType} (MessageId: {MessageId})",
                commandType.Name, message.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing SQS message: {MessageId}", message.MessageId);
            // Message will return to queue after visibility timeout
            // Consider dead-letter queue for persistent failures
        }
    }
}