using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SourceFlow.Stores.EntityFramework;
using SourceFlow.Stores.EntityFramework.Services;

namespace SourceFlow.Stores.EntityFramework.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class EfIdempotencyServiceTests
{
    private IdempotencyDbContext _context = null!;
    private EfIdempotencyService _service = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<IdempotencyDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new IdempotencyDbContext(options);
        _service = new EfIdempotencyService(_context, NullLogger<EfIdempotencyService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task HasProcessedAsync_ReturnsFalse_WhenKeyDoesNotExist()
    {
        // Arrange
        var key = "test-key-1";

        // Act
        var result = await _service.HasProcessedAsync(key);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasProcessedAsync_ReturnsTrue_WhenKeyExists()
    {
        // Arrange
        var key = "test-key-2";
        await _service.MarkAsProcessedAsync(key, TimeSpan.FromMinutes(5));

        // Act
        var result = await _service.HasProcessedAsync(key);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task HasProcessedAsync_ReturnsFalse_WhenKeyExpired()
    {
        // Arrange
        var key = "test-key-3";
        await _service.MarkAsProcessedAsync(key, TimeSpan.FromMilliseconds(-100));

        // Act
        var result = await _service.HasProcessedAsync(key);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task MarkAsProcessedAsync_CreatesNewRecord()
    {
        // Arrange
        var key = "test-key-4";
        var ttl = TimeSpan.FromMinutes(10);

        // Act
        await _service.MarkAsProcessedAsync(key, ttl);

        // Assert
        var record = await _context.IdempotencyRecords.FindAsync(key);
        Assert.That(record, Is.Not.Null);
        Assert.That(record!.IdempotencyKey, Is.EqualTo(key));
        Assert.That(record.ExpiresAt, Is.GreaterThan(DateTime.UtcNow));
    }

    [Test]
    public async Task MarkAsProcessedAsync_UpdatesExistingRecord()
    {
        // Arrange
        var key = "test-key-5";
        await _service.MarkAsProcessedAsync(key, TimeSpan.FromMinutes(5));
        var firstRecord = await _context.IdempotencyRecords.FindAsync(key);
        var firstProcessedAt = firstRecord!.ProcessedAt;

        await Task.Delay(100); // Small delay to ensure different timestamp

        // Act
        await _service.MarkAsProcessedAsync(key, TimeSpan.FromMinutes(10));

        // Assert
        var updatedRecord = await _context.IdempotencyRecords.FindAsync(key);
        Assert.That(updatedRecord, Is.Not.Null);
        Assert.That(updatedRecord!.ProcessedAt, Is.GreaterThanOrEqualTo(firstProcessedAt));
    }

    [Test]
    public async Task RemoveAsync_DeletesRecord()
    {
        // Arrange
        var key = "test-key-6";
        await _service.MarkAsProcessedAsync(key, TimeSpan.FromMinutes(5));

        // Act
        await _service.RemoveAsync(key);

        // Assert
        var record = await _context.IdempotencyRecords.FindAsync(key);
        Assert.That(record, Is.Null);
    }

    [Test]
    public async Task GetStatisticsAsync_ReturnsCorrectCounts()
    {
        // Arrange
        await _service.MarkAsProcessedAsync("key-1", TimeSpan.FromMinutes(5));
        await _service.MarkAsProcessedAsync("key-2", TimeSpan.FromMinutes(5));
        await _service.HasProcessedAsync("key-1"); // Duplicate
        await _service.HasProcessedAsync("key-3"); // New

        // Act
        var stats = await _service.GetStatisticsAsync();

        // Assert
        Assert.That(stats.CacheSize, Is.EqualTo(2));
        Assert.That(stats.TotalChecks, Is.EqualTo(2));
        Assert.That(stats.DuplicatesDetected, Is.EqualTo(1));
        Assert.That(stats.UniqueMessages, Is.EqualTo(1));
    }

    [Test]
    public async Task CleanupExpiredRecordsAsync_RemovesExpiredRecords()
    {
        // Arrange
        await _service.MarkAsProcessedAsync("expired-1", TimeSpan.FromMilliseconds(-100));
        await _service.MarkAsProcessedAsync("expired-2", TimeSpan.FromMilliseconds(-100));
        await _service.MarkAsProcessedAsync("valid-1", TimeSpan.FromMinutes(10));

        // Act
        await _service.CleanupExpiredRecordsAsync();

        // Assert
        var remainingCount = await _context.IdempotencyRecords.CountAsync();
        Assert.That(remainingCount, Is.EqualTo(1));
        
        var validRecord = await _context.IdempotencyRecords.FindAsync("valid-1");
        Assert.That(validRecord, Is.Not.Null);
    }
}
