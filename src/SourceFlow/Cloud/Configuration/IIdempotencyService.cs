using System;
using System.Threading;
using System.Threading.Tasks;

namespace SourceFlow.Cloud.Configuration;

/// <summary>
/// Service for tracking and enforcing idempotency of message processing
/// </summary>
public interface IIdempotencyService
{
    /// <summary>
    /// Check if a message has already been processed
    /// </summary>
    Task<bool> HasProcessedAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a message as processed
    /// </summary>
    Task MarkAsProcessedAsync(string idempotencyKey, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove an idempotency record (for replay scenarios)
    /// </summary>
    Task RemoveAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get statistics about idempotency tracking
    /// </summary>
    Task<IdempotencyStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Statistics about idempotency service
/// </summary>
public class IdempotencyStatistics
{
    public long TotalChecks { get; set; }
    public long DuplicatesDetected { get; set; }
    public long UniqueMessages { get; set; }
    public int CacheSize { get; set; }
}
