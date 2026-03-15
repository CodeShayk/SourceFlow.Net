using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SourceFlow.Cloud.Configuration;

namespace SourceFlow.Core.Tests.Cloud
{
    [TestFixture]
    [Category("Unit")]
    public class InMemoryIdempotencyServiceTests
    {
        private InMemoryIdempotencyService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _service = new InMemoryIdempotencyService(NullLogger<InMemoryIdempotencyService>.Instance);
        }

        // ── HasProcessedAsync ─────────────────────────────────────────────────────

        [Test]
        public async Task HasProcessedAsync_UnknownKey_ReturnsFalse()
        {
            var result = await _service.HasProcessedAsync("unknown-key");

            Assert.That(result, Is.False);
        }

        [Test]
        public async Task HasProcessedAsync_KnownKeyWithinTtl_ReturnsTrue()
        {
            const string key = "processed-key";
            await _service.MarkAsProcessedAsync(key, TimeSpan.FromMinutes(5));

            var result = await _service.HasProcessedAsync(key);

            Assert.That(result, Is.True);
        }

        [Test]
        public async Task HasProcessedAsync_ExpiredKey_ReturnsFalse()
        {
            const string key = "expired-key";
            // Mark as processed with a TTL that has already elapsed
            await _service.MarkAsProcessedAsync(key, TimeSpan.FromMilliseconds(-1));

            var result = await _service.HasProcessedAsync(key);

            Assert.That(result, Is.False);
        }

        // ── MarkAsProcessedAsync ──────────────────────────────────────────────────

        [Test]
        public async Task MarkAsProcessedAsync_StoresKeyWithCorrectTtl()
        {
            const string key = "ttl-key";
            var ttl = TimeSpan.FromMinutes(10);

            await _service.MarkAsProcessedAsync(key, ttl);

            // Immediately after marking, the key should be found
            var result = await _service.HasProcessedAsync(key);
            Assert.That(result, Is.True);
        }

        [Test]
        public async Task MarkAsProcessedAsync_OverwritesExistingRecord()
        {
            const string key = "overwrite-key";

            // Mark as processed then mark again with longer TTL
            await _service.MarkAsProcessedAsync(key, TimeSpan.FromMilliseconds(-100)); // effectively expired
            await _service.MarkAsProcessedAsync(key, TimeSpan.FromMinutes(5)); // fresh

            var result = await _service.HasProcessedAsync(key);
            Assert.That(result, Is.True);
        }

        // ── GetStatisticsAsync ────────────────────────────────────────────────────

        [Test]
        public async Task GetStatisticsAsync_IncrementsTotalChecks()
        {
            await _service.HasProcessedAsync("key-1");
            await _service.HasProcessedAsync("key-2");

            var stats = await _service.GetStatisticsAsync();

            Assert.That(stats.TotalChecks, Is.EqualTo(2));
        }

        [Test]
        public async Task GetStatisticsAsync_IncrementsDuplicatesDetected_WhenKeyAlreadyProcessed()
        {
            const string key = "dup-key";
            await _service.MarkAsProcessedAsync(key, TimeSpan.FromMinutes(5));

            // First check: key was not present before mark, so not a duplicate
            // The duplicate is detected when the key IS found
            await _service.HasProcessedAsync(key); // duplicate detected
            await _service.HasProcessedAsync(key); // duplicate detected again

            var stats = await _service.GetStatisticsAsync();

            Assert.That(stats.DuplicatesDetected, Is.EqualTo(2));
        }

        [Test]
        public async Task GetStatisticsAsync_UniqueMessages_EqualsChecksMinusDuplicates()
        {
            const string key = "stats-key";
            await _service.MarkAsProcessedAsync(key, TimeSpan.FromMinutes(5));

            await _service.HasProcessedAsync("fresh-key"); // not a duplicate
            await _service.HasProcessedAsync(key);         // duplicate

            var stats = await _service.GetStatisticsAsync();

            Assert.That(stats.UniqueMessages, Is.EqualTo(stats.TotalChecks - stats.DuplicatesDetected));
        }

        // ── RemoveAsync ───────────────────────────────────────────────────────────

        [Test]
        public async Task RemoveAsync_RemovesKey_SubsequentCheckReturnsFalse()
        {
            const string key = "remove-key";
            await _service.MarkAsProcessedAsync(key, TimeSpan.FromMinutes(5));

            await _service.RemoveAsync(key);

            var result = await _service.HasProcessedAsync(key);
            Assert.That(result, Is.False);
        }
    }
}
