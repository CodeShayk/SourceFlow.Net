using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.DeadLetter;
using SourceFlow.Cloud.Observability;
using System.Text.Json;

namespace SourceFlow.Cloud.Azure.Monitoring;

/// <summary>
/// Background service that monitors Azure Service Bus dead letter queues/subscriptions
/// </summary>
public class AzureDeadLetterMonitor : BackgroundService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly CloudMetrics _cloudMetrics;
    private readonly ILogger<AzureDeadLetterMonitor> _logger;
    private readonly AzureDeadLetterMonitorOptions _options;

    public AzureDeadLetterMonitor(
        ServiceBusClient serviceBusClient,
        IDeadLetterStore deadLetterStore,
        CloudMetrics cloudMetrics,
        ILogger<AzureDeadLetterMonitor> logger,
        AzureDeadLetterMonitorOptions options)
    {
        _serviceBusClient = serviceBusClient;
        _deadLetterStore = deadLetterStore;
        _cloudMetrics = cloudMetrics;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Azure Dead Letter Monitor is disabled");
            return;
        }

        if (_options.DeadLetterSources == null || !_options.DeadLetterSources.Any())
        {
            _logger.LogWarning("No dead letter sources configured for monitoring");
            return;
        }

        _logger.LogInformation("Starting Azure Dead Letter Monitor for {Count} sources",
            _options.DeadLetterSources.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var source in _options.DeadLetterSources)
                {
                    await MonitorDeadLetterSource(source, stoppingToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(_options.CheckIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in dead letter monitoring loop");
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
        }

        _logger.LogInformation("Azure Dead Letter Monitor stopped");
    }

    private async Task MonitorDeadLetterSource(
        DeadLetterSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            ServiceBusReceiver receiver;

            if (string.IsNullOrEmpty(source.SubscriptionName))
            {
                // Queue dead letter
                receiver = _serviceBusClient.CreateReceiver(source.QueueOrTopicName,
                    new ServiceBusReceiverOptions
                    {
                        SubQueue = SubQueue.DeadLetter,
                        ReceiveMode = ServiceBusReceiveMode.PeekLock
                    });
            }
            else
            {
                // Topic subscription dead letter
                receiver = _serviceBusClient.CreateReceiver(source.QueueOrTopicName,
                    source.SubscriptionName,
                    new ServiceBusReceiverOptions
                    {
                        SubQueue = SubQueue.DeadLetter,
                        ReceiveMode = ServiceBusReceiveMode.PeekLock
                    });
            }

            await using (receiver)
            {
                var messages = await receiver.ReceiveMessagesAsync(
                    _options.BatchSize,
                    TimeSpan.FromSeconds(5),
                    cancellationToken);

                if (messages.Any())
                {
                    _logger.LogInformation("Found {Count} messages in dead letter: {Source}",
                        messages.Count, GetSourceName(source));

                    _cloudMetrics.UpdateDlqDepth(messages.Count);

                    foreach (var message in messages)
                    {
                        await ProcessDeadLetter(message, source, receiver, cancellationToken);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring dead letter source: {Source}",
                GetSourceName(source));
        }
    }

    private async Task ProcessDeadLetter(
        ServiceBusReceivedMessage message,
        DeadLetterSource source,
        ServiceBusReceiver receiver,
        CancellationToken cancellationToken)
    {
        try
        {
            var messageType = message.ApplicationProperties.TryGetValue("CommandType", out var ct)
                ? ct?.ToString()
                : message.ApplicationProperties.TryGetValue("EventType", out var et)
                    ? et?.ToString()
                    : "Unknown";

            var record = new DeadLetterRecord
            {
                MessageId = message.MessageId,
                Body = message.Body.ToString(),
                MessageType = messageType ?? "Unknown",
                Reason = message.DeadLetterReason ?? "Unknown",
                ErrorDescription = message.DeadLetterErrorDescription ?? "No description provided",
                OriginalSource = GetSourceName(source),
                DeadLetterSource = $"{GetSourceName(source)}/$DeadLetterQueue",
                CloudProvider = "azure",
                DeadLetteredAt = DateTime.UtcNow,
                DeliveryCount = (int)message.DeliveryCount,
                Metadata = new Dictionary<string, string>()
            };

            foreach (var prop in message.ApplicationProperties)
            {
                record.Metadata[prop.Key] = prop.Value?.ToString() ?? string.Empty;
            }

            if (_options.StoreRecords)
            {
                await _deadLetterStore.SaveAsync(record, cancellationToken);
                _logger.LogInformation(
                    "Stored dead letter record: {MessageId}, Type: {MessageType}, Reason: {Reason}",
                    record.MessageId, record.MessageType, record.Reason);
            }

            if (_options.SendAlerts && _cloudMetrics != null)
            {
                _logger.LogWarning(
                    "ALERT: Dead letter message detected. Source: {Source}, Reason: {Reason}",
                    GetSourceName(source), record.Reason);
            }

            if (_options.DeleteAfterProcessing)
            {
                await receiver.CompleteMessageAsync(message, cancellationToken);
                _logger.LogDebug("Deleted message from DLQ: {MessageId}", message.MessageId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing dead letter message: {MessageId}",
                message.MessageId);
        }
    }

    /// <summary>
    /// Replay messages from dead letter back to the original source
    /// </summary>
    public async Task<int> ReplayMessagesAsync(
        DeadLetterSource source,
        int maxMessages = 10,
        CancellationToken cancellationToken = default)
    {
        var replayedCount = 0;

        try
        {
            _logger.LogInformation("Starting message replay from DLQ: {Source}, MaxMessages: {Max}",
                GetSourceName(source), maxMessages);

            ServiceBusReceiver receiver;
            ServiceBusSender sender;

            if (string.IsNullOrEmpty(source.SubscriptionName))
            {
                receiver = _serviceBusClient.CreateReceiver(source.QueueOrTopicName,
                    new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
                sender = _serviceBusClient.CreateSender(source.QueueOrTopicName);
            }
            else
            {
                receiver = _serviceBusClient.CreateReceiver(source.QueueOrTopicName,
                    source.SubscriptionName,
                    new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
                sender = _serviceBusClient.CreateSender(source.QueueOrTopicName);
            }

            await using (receiver)
            await using (sender)
            {
                var messages = await receiver.ReceiveMessagesAsync(maxMessages,
                    TimeSpan.FromSeconds(5), cancellationToken);

                foreach (var message in messages)
                {
                    var newMessage = new ServiceBusMessage(message.Body)
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Subject = message.Subject,
                        ContentType = message.ContentType,
                        SessionId = message.SessionId
                    };

                    foreach (var prop in message.ApplicationProperties)
                    {
                        newMessage.ApplicationProperties[prop.Key] = prop.Value;
                    }

                    await sender.SendMessageAsync(newMessage, cancellationToken);
                    await receiver.CompleteMessageAsync(message, cancellationToken);

                    await _deadLetterStore.MarkAsReplayedAsync(message.MessageId, cancellationToken);

                    replayedCount++;
                    _logger.LogInformation("Replayed message {MessageId} from DLQ to {Source}",
                        message.MessageId, GetSourceName(source));
                }
            }

            _logger.LogInformation("Message replay complete. Replayed {Count} messages", replayedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error replaying messages from DLQ");
            throw;
        }

        return replayedCount;
    }

    private static string GetSourceName(DeadLetterSource source)
    {
        return string.IsNullOrEmpty(source.SubscriptionName)
            ? source.QueueOrTopicName
            : $"{source.QueueOrTopicName}/{source.SubscriptionName}";
    }
}

/// <summary>
/// Configuration options for Azure Dead Letter Monitor
/// </summary>
public class AzureDeadLetterMonitorOptions
{
    public bool Enabled { get; set; } = true;
    public List<DeadLetterSource> DeadLetterSources { get; set; } = new();
    public int CheckIntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 10;
    public bool StoreRecords { get; set; } = true;
    public bool SendAlerts { get; set; } = true;
    public bool DeleteAfterProcessing { get; set; } = false;
}

public class DeadLetterSource
{
    public string QueueOrTopicName { get; set; } = string.Empty;
    public string? SubscriptionName { get; set; }
}
