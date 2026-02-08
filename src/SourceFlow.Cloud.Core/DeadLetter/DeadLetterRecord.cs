namespace SourceFlow.Cloud.Core.DeadLetter;

/// <summary>
/// Represents a message that has been moved to dead letter queue
/// </summary>
public class DeadLetterRecord
{
    /// <summary>
    /// Unique identifier for this dead letter record
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Original message ID
    /// </summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// Message body (potentially encrypted)
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Message type (command or event type name)
    /// </summary>
    public string MessageType { get; set; } = string.Empty;

    /// <summary>
    /// Reason for dead lettering
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Detailed error description
    /// </summary>
    public string? ErrorDescription { get; set; }

    /// <summary>
    /// Original queue/topic name
    /// </summary>
    public string OriginalSource { get; set; } = string.Empty;

    /// <summary>
    /// Dead letter queue/topic name
    /// </summary>
    public string DeadLetterSource { get; set; } = string.Empty;

    /// <summary>
    /// Cloud provider (AWS, Azure)
    /// </summary>
    public string CloudProvider { get; set; } = string.Empty;

    /// <summary>
    /// When the message was dead lettered
    /// </summary>
    public DateTime DeadLetteredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of delivery attempts before dead lettering
    /// </summary>
    public int DeliveryCount { get; set; }

    /// <summary>
    /// Last exception that caused dead lettering
    /// </summary>
    public string? ExceptionType { get; set; }

    /// <summary>
    /// Exception message
    /// </summary>
    public string? ExceptionMessage { get; set; }

    /// <summary>
    /// Exception stack trace
    /// </summary>
    public string? ExceptionStackTrace { get; set; }

    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Whether this message has been replayed
    /// </summary>
    public bool Replayed { get; set; } = false;

    /// <summary>
    /// When the message was replayed (if applicable)
    /// </summary>
    public DateTime? ReplayedAt { get; set; }
}
