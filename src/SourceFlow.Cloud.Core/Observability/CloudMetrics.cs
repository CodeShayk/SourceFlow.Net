using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.Core.Observability;

/// <summary>
/// Provides metrics for cloud messaging operations
/// </summary>
public class CloudMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly ILogger<CloudMetrics> _logger;

    // Counters
    private readonly Counter<long> _commandsDispatched;
    private readonly Counter<long> _commandsProcessed;
    private readonly Counter<long> _commandsProcessed_Success;
    private readonly Counter<long> _commandsFailed;
    private readonly Counter<long> _eventsPublished;
    private readonly Counter<long> _eventsReceived;
    private readonly Counter<long> _duplicatesDetected;

    // Histograms
    private readonly Histogram<double> _commandDispatchDuration;
    private readonly Histogram<double> _commandProcessingDuration;
    private readonly Histogram<double> _eventPublishDuration;
    private readonly Histogram<int> _messageSize;

    // Gauges (Observable)
    private int _currentQueueDepth = 0;
    private int _currentDlqDepth = 0;
    private int _activeProcessors = 0;

    public CloudMetrics(ILogger<CloudMetrics> logger)
    {
        _logger = logger;
        _meter = new Meter("SourceFlow.Cloud", "1.0.0");

        // Initialize counters
        _commandsDispatched = _meter.CreateCounter<long>(
            "sourceflow.commands.dispatched",
            unit: "{command}",
            description: "Number of commands dispatched to cloud");

        _commandsProcessed = _meter.CreateCounter<long>(
            "sourceflow.commands.processed",
            unit: "{command}",
            description: "Number of commands processed from cloud");

        _commandsProcessed_Success = _meter.CreateCounter<long>(
            "sourceflow.commands.processed.success",
            unit: "{command}",
            description: "Number of commands successfully processed");

        _commandsFailed = _meter.CreateCounter<long>(
            "sourceflow.commands.failed",
            unit: "{command}",
            description: "Number of commands that failed processing");

        _eventsPublished = _meter.CreateCounter<long>(
            "sourceflow.events.published",
            unit: "{event}",
            description: "Number of events published to cloud");

        _eventsReceived = _meter.CreateCounter<long>(
            "sourceflow.events.received",
            unit: "{event}",
            description: "Number of events received from cloud");

        _duplicatesDetected = _meter.CreateCounter<long>(
            "sourceflow.duplicates.detected",
            unit: "{message}",
            description: "Number of duplicate messages detected via idempotency");

        // Initialize histograms
        _commandDispatchDuration = _meter.CreateHistogram<double>(
            "sourceflow.command.dispatch.duration",
            unit: "ms",
            description: "Command dispatch duration in milliseconds");

        _commandProcessingDuration = _meter.CreateHistogram<double>(
            "sourceflow.command.processing.duration",
            unit: "ms",
            description: "Command processing duration in milliseconds");

        _eventPublishDuration = _meter.CreateHistogram<double>(
            "sourceflow.event.publish.duration",
            unit: "ms",
            description: "Event publish duration in milliseconds");

        _messageSize = _meter.CreateHistogram<int>(
            "sourceflow.message.size",
            unit: "bytes",
            description: "Message payload size in bytes");

        // Initialize observable gauges
        _meter.CreateObservableGauge(
            "sourceflow.queue.depth",
            () => _currentQueueDepth,
            unit: "{message}",
            description: "Current queue depth");

        _meter.CreateObservableGauge(
            "sourceflow.dlq.depth",
            () => _currentDlqDepth,
            unit: "{message}",
            description: "Current dead letter queue depth");

        _meter.CreateObservableGauge(
            "sourceflow.processors.active",
            () => _activeProcessors,
            unit: "{processor}",
            description: "Number of active message processors");
    }

    public void RecordCommandDispatched(string commandType, string destination, string cloudProvider)
    {
        _commandsDispatched.Add(1,
            new KeyValuePair<string, object?>("command.type", commandType),
            new KeyValuePair<string, object?>("destination", destination),
            new KeyValuePair<string, object?>("cloud.provider", cloudProvider));
    }

    public void RecordCommandProcessed(string commandType, string source, string cloudProvider, bool success)
    {
        _commandsProcessed.Add(1,
            new KeyValuePair<string, object?>("command.type", commandType),
            new KeyValuePair<string, object?>("source", source),
            new KeyValuePair<string, object?>("cloud.provider", cloudProvider),
            new KeyValuePair<string, object?>("success", success));

        if (success)
        {
            _commandsProcessed_Success.Add(1,
                new KeyValuePair<string, object?>("command.type", commandType),
                new KeyValuePair<string, object?>("cloud.provider", cloudProvider));
        }
        else
        {
            _commandsFailed.Add(1,
                new KeyValuePair<string, object?>("command.type", commandType),
                new KeyValuePair<string, object?>("cloud.provider", cloudProvider));
        }
    }

    public void RecordEventPublished(string eventType, string destination, string cloudProvider)
    {
        _eventsPublished.Add(1,
            new KeyValuePair<string, object?>("event.type", eventType),
            new KeyValuePair<string, object?>("destination", destination),
            new KeyValuePair<string, object?>("cloud.provider", cloudProvider));
    }

    public void RecordEventReceived(string eventType, string source, string cloudProvider)
    {
        _eventsReceived.Add(1,
            new KeyValuePair<string, object?>("event.type", eventType),
            new KeyValuePair<string, object?>("source", source),
            new KeyValuePair<string, object?>("cloud.provider", cloudProvider));
    }

    public void RecordDuplicateDetected(string messageType, string cloudProvider)
    {
        _duplicatesDetected.Add(1,
            new KeyValuePair<string, object?>("message.type", messageType),
            new KeyValuePair<string, object?>("cloud.provider", cloudProvider));
    }

    public void RecordDispatchDuration(double durationMs, string commandType, string cloudProvider)
    {
        _commandDispatchDuration.Record(durationMs,
            new KeyValuePair<string, object?>("command.type", commandType),
            new KeyValuePair<string, object?>("cloud.provider", cloudProvider));
    }

    public void RecordProcessingDuration(double durationMs, string commandType, string cloudProvider)
    {
        _commandProcessingDuration.Record(durationMs,
            new KeyValuePair<string, object?>("command.type", commandType),
            new KeyValuePair<string, object?>("cloud.provider", cloudProvider));
    }

    public void RecordPublishDuration(double durationMs, string eventType, string cloudProvider)
    {
        _eventPublishDuration.Record(durationMs,
            new KeyValuePair<string, object?>("event.type", eventType),
            new KeyValuePair<string, object?>("cloud.provider", cloudProvider));
    }

    public void RecordMessageSize(int sizeBytes, string messageType, string cloudProvider)
    {
        _messageSize.Record(sizeBytes,
            new KeyValuePair<string, object?>("message.type", messageType),
            new KeyValuePair<string, object?>("cloud.provider", cloudProvider));
    }

    public void UpdateQueueDepth(int depth) => _currentQueueDepth = depth;
    public void UpdateDlqDepth(int depth) => _currentDlqDepth = depth;
    public void UpdateActiveProcessors(int count) => _activeProcessors = count;

    public void Dispose()
    {
        _meter?.Dispose();
    }
}
