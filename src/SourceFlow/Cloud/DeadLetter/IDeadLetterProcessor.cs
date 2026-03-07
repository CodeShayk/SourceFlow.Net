using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SourceFlow.Cloud.DeadLetter;

/// <summary>
/// Service for processing dead letter queues
/// </summary>
public interface IDeadLetterProcessor
{
    /// <summary>
    /// Process messages from a dead letter queue
    /// </summary>
    Task ProcessDeadLetterQueueAsync(
        string queueOrTopicName,
        DeadLetterProcessingOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replay messages from dead letter queue back to original queue
    /// </summary>
    Task ReplayMessagesAsync(
        string queueOrTopicName,
        Func<DeadLetterRecord, bool> filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get statistics about a dead letter queue
    /// </summary>
    Task<DeadLetterStatistics> GetStatisticsAsync(
        string queueOrTopicName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for dead letter processing
/// </summary>
public class DeadLetterProcessingOptions
{
    /// <summary>
    /// Maximum number of messages to process per batch
    /// </summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>
    /// Whether to store dead letter records
    /// </summary>
    public bool StoreRecords { get; set; } = true;

    /// <summary>
    /// Whether to send alerts for new dead letters
    /// </summary>
    public bool SendAlerts { get; set; } = true;

    /// <summary>
    /// Alert threshold (send alert if count exceeds this)
    /// </summary>
    public int AlertThreshold { get; set; } = 10;

    /// <summary>
    /// Whether to automatically delete processed dead letters
    /// </summary>
    public bool DeleteAfterProcessing { get; set; } = false;
}

/// <summary>
/// Statistics about dead letter queue
/// </summary>
public class DeadLetterStatistics
{
    public string QueueOrTopicName { get; set; } = string.Empty;
    public string CloudProvider { get; set; } = string.Empty;
    public int TotalMessages { get; set; }
    public int MessagesByReason { get; set; }
    public DateTime? OldestMessage { get; set; }
    public DateTime? NewestMessage { get; set; }
    public Dictionary<string, int> ReasonCounts { get; set; } = new();
    public Dictionary<string, int> MessageTypeCounts { get; set; } = new();
}
