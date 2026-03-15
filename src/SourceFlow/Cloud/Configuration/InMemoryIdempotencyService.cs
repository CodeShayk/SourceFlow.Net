using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.Configuration;

/// <summary>
/// In-memory implementation of idempotency service (suitable for single-instance deployments)
/// </summary>
public class InMemoryIdempotencyService : IIdempotencyService
{
    private readonly ConcurrentDictionary<string, IdempotencyRecord> _records = new();
    private readonly ILogger<InMemoryIdempotencyService> _logger;
    private long _totalChecks = 0;
    private long _duplicatesDetected = 0;

    public InMemoryIdempotencyService(ILogger<InMemoryIdempotencyService> logger)
    {
        _logger = logger;
    }

    public Task<bool> HasProcessedAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _totalChecks);

        if (_records.TryGetValue(idempotencyKey, out var record))
        {
            if (record.ExpiresAt > DateTime.UtcNow)
            {
                Interlocked.Increment(ref _duplicatesDetected);
                _logger.LogDebug("Duplicate message detected: {IdempotencyKey}", idempotencyKey);
                return Task.FromResult(true);
            }
            else
            {
                // Expired, remove it
                _records.TryRemove(idempotencyKey, out _);
            }
        }

        return Task.FromResult(false);
    }

    public Task MarkAsProcessedAsync(string idempotencyKey, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var record = new IdempotencyRecord
        {
            Key = idempotencyKey,
            ProcessedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(ttl)
        };

        _records[idempotencyKey] = record;

        _logger.LogTrace("Marked message as processed: {IdempotencyKey}, TTL: {TTL}s",
            idempotencyKey, ttl.TotalSeconds);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        _records.TryRemove(idempotencyKey, out _);
        _logger.LogDebug("Removed idempotency record: {IdempotencyKey}", idempotencyKey);
        return Task.CompletedTask;
    }

    public Task<IdempotencyStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new IdempotencyStatistics
        {
            TotalChecks = _totalChecks,
            DuplicatesDetected = _duplicatesDetected,
            UniqueMessages = _totalChecks - _duplicatesDetected,
            CacheSize = _records.Count
        });
    }

    internal Task RunCleanupAsync(CancellationToken cancellationToken) =>
        CleanupExpiredRecordsAsync(cancellationToken);

    private async Task CleanupExpiredRecordsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);

                var now = DateTime.UtcNow;
                var expiredKeys = _records
                    .Where(kvp => kvp.Value.ExpiresAt <= now)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _records.TryRemove(key, out _);
                }

                if (expiredKeys.Count > 0)
                {
                    _logger.LogDebug("Cleaned up {Count} expired idempotency records", expiredKeys.Count);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested; exit the loop cleanly
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during idempotency cleanup");
            }
        }
    }

    private class IdempotencyRecord
    {
        public string Key { get; set; } = string.Empty;
        public DateTime ProcessedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
