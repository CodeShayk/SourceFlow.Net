#nullable enable

using System;

namespace SourceFlow.Stores.EntityFramework.Models;

/// <summary>
/// Entity Framework model for idempotency tracking
/// </summary>
public class IdempotencyRecord
{
    /// <summary>
    /// Unique idempotency key (message ID or correlation ID)
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// When the message was first processed
    /// </summary>
    public DateTime ProcessedAt { get; set; }

    /// <summary>
    /// When this record expires and can be cleaned up
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Optional metadata about the processed message
    /// </summary>
    public string? MessageType { get; set; }

    /// <summary>
    /// Cloud provider (AWS, Azure, etc.)
    /// </summary>
    public string? CloudProvider { get; set; }
}
