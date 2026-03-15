using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SourceFlow.Cloud.DeadLetter;

/// <summary>
/// Persistent storage for dead letter records
/// </summary>
public interface IDeadLetterStore
{
    /// <summary>
    /// Save a dead letter record
    /// </summary>
    Task SaveAsync(DeadLetterRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a dead letter record by ID
    /// </summary>
    Task<DeadLetterRecord?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query dead letter records
    /// </summary>
    Task<IEnumerable<DeadLetterRecord>> QueryAsync(
        DeadLetterQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get count of dead letter records matching query
    /// </summary>
    Task<int> GetCountAsync(DeadLetterQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a dead letter record as replayed
    /// </summary>
    Task MarkAsReplayedAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a dead letter record
    /// </summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete old records (cleanup)
    /// </summary>
    Task DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default);
}

/// <summary>
/// Query parameters for dead letter records
/// </summary>
public class DeadLetterQuery
{
    public string? MessageType { get; set; }
    public string? Reason { get; set; }
    public string? CloudProvider { get; set; }
    public string? OriginalSource { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public bool? Replayed { get; set; }
    public int Skip { get; set; } = 0;
    public int Take { get; set; } = 100;
}
