using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Cloud.DeadLetter;

/// <summary>
/// In-memory implementation of dead letter store (for testing/development)
/// </summary>
public class InMemoryDeadLetterStore : IDeadLetterStore
{
    private readonly ConcurrentDictionary<string, DeadLetterRecord> _records = new();
    private readonly ILogger<InMemoryDeadLetterStore> _logger;

    public InMemoryDeadLetterStore(ILogger<InMemoryDeadLetterStore> logger)
    {
        _logger = logger;
    }

    public Task SaveAsync(DeadLetterRecord record, CancellationToken cancellationToken = default)
    {
        _records[record.Id] = record;
        _logger.LogDebug("Saved dead letter record: {Id}, Type: {MessageType}, Reason: {Reason}",
            record.Id, record.MessageType, record.Reason);
        return Task.CompletedTask;
    }

    public Task<DeadLetterRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        _records.TryGetValue(id, out var record);
        return Task.FromResult(record);
    }

    public Task<IEnumerable<DeadLetterRecord>> QueryAsync(
        DeadLetterQuery query,
        CancellationToken cancellationToken = default)
    {
        var results = _records.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(query.MessageType))
            results = results.Where(r => r.MessageType == query.MessageType);

        if (!string.IsNullOrEmpty(query.Reason))
            results = results.Where(r => r.Reason.IndexOf(query.Reason, StringComparison.OrdinalIgnoreCase) >= 0);

        if (!string.IsNullOrEmpty(query.CloudProvider))
            results = results.Where(r => r.CloudProvider == query.CloudProvider);

        if (!string.IsNullOrEmpty(query.OriginalSource))
            results = results.Where(r => r.OriginalSource == query.OriginalSource);

        if (query.FromDate.HasValue)
            results = results.Where(r => r.DeadLetteredAt >= query.FromDate.Value);

        if (query.ToDate.HasValue)
            results = results.Where(r => r.DeadLetteredAt <= query.ToDate.Value);

        if (query.Replayed.HasValue)
            results = results.Where(r => r.Replayed == query.Replayed.Value);

        results = results
            .OrderByDescending(r => r.DeadLetteredAt)
            .Skip(query.Skip)
            .Take(query.Take);

        return Task.FromResult(results);
    }

    public Task<int> GetCountAsync(DeadLetterQuery query, CancellationToken cancellationToken = default)
    {
        var results = _records.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(query.MessageType))
            results = results.Where(r => r.MessageType == query.MessageType);

        if (!string.IsNullOrEmpty(query.Reason))
            results = results.Where(r => r.Reason.IndexOf(query.Reason, StringComparison.OrdinalIgnoreCase) >= 0);

        if (!string.IsNullOrEmpty(query.CloudProvider))
            results = results.Where(r => r.CloudProvider == query.CloudProvider);

        if (query.FromDate.HasValue)
            results = results.Where(r => r.DeadLetteredAt >= query.FromDate.Value);

        if (query.ToDate.HasValue)
            results = results.Where(r => r.DeadLetteredAt <= query.ToDate.Value);

        if (query.Replayed.HasValue)
            results = results.Where(r => r.Replayed == query.Replayed.Value);

        return Task.FromResult(results.Count());
    }

    public Task MarkAsReplayedAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_records.TryGetValue(id, out var record))
        {
            record.Replayed = true;
            record.ReplayedAt = DateTime.UtcNow;
            _logger.LogInformation("Marked dead letter record as replayed: {Id}", id);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _records.TryRemove(id, out _);
        _logger.LogDebug("Deleted dead letter record: {Id}", id);
        return Task.CompletedTask;
    }

    public Task DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default)
    {
        var toDelete = _records.Values
            .Where(r => r.DeadLetteredAt < cutoffDate)
            .Select(r => r.Id)
            .ToList();

        foreach (var id in toDelete)
        {
            _records.TryRemove(id, out _);
        }

        if (toDelete.Count > 0)
        {
            _logger.LogInformation("Deleted {Count} old dead letter records (older than {Date})",
                toDelete.Count, cutoffDate);
        }

        return Task.CompletedTask;
    }
}
