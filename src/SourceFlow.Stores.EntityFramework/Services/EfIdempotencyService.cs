#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Stores.EntityFramework.Models;

namespace SourceFlow.Stores.EntityFramework.Services;

/// <summary>
/// SQL-based idempotency service for multi-instance deployments
/// Uses database transactions to ensure thread-safe duplicate detection
/// </summary>
public class EfIdempotencyService : IIdempotencyService
{
    private readonly IdempotencyDbContext _context;
    private readonly ILogger<EfIdempotencyService> _logger;
    private long _totalChecks = 0;
    private long _duplicatesDetected = 0;

    public EfIdempotencyService(
        IdempotencyDbContext context,
        ILogger<EfIdempotencyService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<bool> HasProcessedAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _totalChecks);

        try
        {
            var now = DateTime.UtcNow;
            
            // Check if record exists and hasn't expired
            var exists = await _context.IdempotencyRecords
                .Where(r => r.IdempotencyKey == idempotencyKey && r.ExpiresAt > now)
                .AnyAsync(cancellationToken);

            if (exists)
            {
                Interlocked.Increment(ref _duplicatesDetected);
                _logger.LogDebug("Duplicate message detected: {IdempotencyKey}", idempotencyKey);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking idempotency for key: {IdempotencyKey}", idempotencyKey);
            throw;
        }
    }

    public async Task MarkAsProcessedAsync(string idempotencyKey, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            var record = new IdempotencyRecord
            {
                IdempotencyKey = idempotencyKey,
                ProcessedAt = now,
                ExpiresAt = now.Add(ttl)
            };

            // Use upsert pattern to handle race conditions
            var existing = await _context.IdempotencyRecords
                .Where(r => r.IdempotencyKey == idempotencyKey)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing != null)
            {
                // Update existing record
                existing.ProcessedAt = record.ProcessedAt;
                existing.ExpiresAt = record.ExpiresAt;
            }
            else
            {
                // Insert new record
                await _context.IdempotencyRecords.AddAsync(record, cancellationToken);
            }

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogTrace("Marked message as processed: {IdempotencyKey}, TTL: {TTL}s",
                idempotencyKey, ttl.TotalSeconds);
        }
        catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
        {
            // Another instance already inserted this key - this is expected in race conditions
            _logger.LogDebug("Concurrent insert detected for key: {IdempotencyKey}", idempotencyKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking message as processed: {IdempotencyKey}", idempotencyKey);
            throw;
        }
    }

    public async Task RemoveAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var record = await _context.IdempotencyRecords
                .Where(r => r.IdempotencyKey == idempotencyKey)
                .FirstOrDefaultAsync(cancellationToken);

            if (record != null)
            {
                _context.IdempotencyRecords.Remove(record);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Removed idempotency record: {IdempotencyKey}", idempotencyKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing idempotency record: {IdempotencyKey}", idempotencyKey);
            throw;
        }
    }

    public async Task<IdempotencyStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cacheSize = await _context.IdempotencyRecords.CountAsync(cancellationToken);

            return new IdempotencyStatistics
            {
                TotalChecks = _totalChecks,
                DuplicatesDetected = _duplicatesDetected,
                UniqueMessages = _totalChecks - _duplicatesDetected,
                CacheSize = cacheSize
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting idempotency statistics");
            throw;
        }
    }

    /// <summary>
    /// Cleanup expired records (should be called periodically by a background job)
    /// </summary>
    public async Task CleanupExpiredRecordsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            
            // Delete expired records in batches to avoid long-running transactions
            var expiredRecords = await _context.IdempotencyRecords
                .Where(r => r.ExpiresAt <= now)
                .Take(1000)
                .ToListAsync(cancellationToken);

            if (expiredRecords.Count > 0)
            {
                _context.IdempotencyRecords.RemoveRange(expiredRecords);
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Cleaned up {Count} expired idempotency records", expiredRecords.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during idempotency cleanup");
            throw;
        }
    }

    private bool IsDuplicateKeyException(DbUpdateException ex)
    {
        // Check for duplicate key violations across different database providers
        var message = ex.InnerException?.Message ?? ex.Message;
        
        return message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("UNIQUE KEY", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
    }
}
