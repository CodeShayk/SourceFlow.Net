using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SourceFlow.Cloud.DeadLetter;

namespace SourceFlow.Core.Tests.Cloud
{
    [TestFixture]
    [Category("Unit")]
    public class InMemoryDeadLetterStoreTests
    {
        private InMemoryDeadLetterStore _store = null!;

        [SetUp]
        public void SetUp()
        {
            _store = new InMemoryDeadLetterStore(NullLogger<InMemoryDeadLetterStore>.Instance);
        }

        // ── SaveAsync / GetAsync ──────────────────────────────────────────────────

        [Test]
        public async Task SaveAsync_PersistsRecord_GetAsyncReturnsIt()
        {
            var record = MakeRecord();
            await _store.SaveAsync(record);

            var result = await _store.GetAsync(record.Id);

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Id, Is.EqualTo(record.Id));
            Assert.That(result.MessageType, Is.EqualTo(record.MessageType));
        }

        [Test]
        public async Task GetAsync_UnknownId_ReturnsNull()
        {
            var result = await _store.GetAsync("does-not-exist");

            Assert.That(result, Is.Null);
        }

        // ── QueryAsync filters ────────────────────────────────────────────────────

        [Test]
        public async Task QueryAsync_FilterByMessageType_ReturnsOnlyMatchingRecords()
        {
            await _store.SaveAsync(MakeRecord(messageType: "OrderPlaced"));
            await _store.SaveAsync(MakeRecord(messageType: "OrderPlaced"));
            await _store.SaveAsync(MakeRecord(messageType: "PaymentProcessed"));

            var results = (await _store.QueryAsync(new DeadLetterQuery { MessageType = "OrderPlaced" })).ToList();

            Assert.That(results.Count, Is.EqualTo(2));
            Assert.That(results.All(r => r.MessageType == "OrderPlaced"), Is.True);
        }

        [Test]
        public async Task QueryAsync_FilterByReason_ReturnsOnlyMatchingRecords()
        {
            await _store.SaveAsync(MakeRecord(reason: "ProcessingError"));
            await _store.SaveAsync(MakeRecord(reason: "DeadLetterQueueThresholdExceeded"));

            var results = (await _store.QueryAsync(new DeadLetterQuery { Reason = "ProcessingError" })).ToList();

            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(results[0].Reason, Is.EqualTo("ProcessingError"));
        }

        [Test]
        public async Task QueryAsync_FilterByCloudProvider_ReturnsOnlyMatchingRecords()
        {
            await _store.SaveAsync(MakeRecord(cloudProvider: "aws"));
            await _store.SaveAsync(MakeRecord(cloudProvider: "azure"));

            var results = (await _store.QueryAsync(new DeadLetterQuery { CloudProvider = "azure" })).ToList();

            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(results[0].CloudProvider, Is.EqualTo("azure"));
        }

        [Test]
        public async Task QueryAsync_FilterByDateRange_ReturnsOnlyRecordsInRange()
        {
            var past = DateTime.UtcNow.AddHours(-2);
            var recent = DateTime.UtcNow;
            var future = DateTime.UtcNow.AddHours(2);

            await _store.SaveAsync(MakeRecord(deadLetteredAt: past));
            await _store.SaveAsync(MakeRecord(deadLetteredAt: recent));

            var results = (await _store.QueryAsync(new DeadLetterQuery
            {
                FromDate = DateTime.UtcNow.AddHours(-1),
                ToDate = DateTime.UtcNow.AddHours(1)
            })).ToList();

            Assert.That(results.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task QueryAsync_FilterByReplayedFlag_ReturnsOnlyMatchingRecords()
        {
            var notReplayed = MakeRecord();
            var replayed = MakeRecord();
            replayed.Replayed = true;

            await _store.SaveAsync(notReplayed);
            await _store.SaveAsync(replayed);

            var notReplayedResults = (await _store.QueryAsync(new DeadLetterQuery { Replayed = false })).ToList();
            var replayedResults = (await _store.QueryAsync(new DeadLetterQuery { Replayed = true })).ToList();

            Assert.That(notReplayedResults.All(r => !r.Replayed), Is.True);
            Assert.That(replayedResults.All(r => r.Replayed), Is.True);
        }

        [Test]
        public async Task QueryAsync_Pagination_SkipAndTakeRespected()
        {
            for (int i = 0; i < 5; i++)
                await _store.SaveAsync(MakeRecord(messageType: "PaginationTest"));

            var page1 = (await _store.QueryAsync(new DeadLetterQuery
            {
                MessageType = "PaginationTest",
                Skip = 0,
                Take = 2
            })).ToList();

            var page2 = (await _store.QueryAsync(new DeadLetterQuery
            {
                MessageType = "PaginationTest",
                Skip = 2,
                Take = 2
            })).ToList();

            Assert.That(page1.Count, Is.EqualTo(2));
            Assert.That(page2.Count, Is.EqualTo(2));

            // Pages should not overlap
            var page1Ids = page1.Select(r => r.Id).ToHashSet();
            var page2Ids = page2.Select(r => r.Id).ToHashSet();
            Assert.That(page1Ids.Intersect(page2Ids), Is.Empty);
        }

        // ── GetCountAsync ─────────────────────────────────────────────────────────

        [Test]
        public async Task GetCountAsync_ReturnsCorrectCountForFilter()
        {
            await _store.SaveAsync(MakeRecord(messageType: "CountTest"));
            await _store.SaveAsync(MakeRecord(messageType: "CountTest"));
            await _store.SaveAsync(MakeRecord(messageType: "OtherType"));

            var count = await _store.GetCountAsync(new DeadLetterQuery { MessageType = "CountTest" });

            Assert.That(count, Is.EqualTo(2));
        }

        // ── MarkAsReplayedAsync ───────────────────────────────────────────────────

        [Test]
        public async Task MarkAsReplayedAsync_SetsReplayedToTrue()
        {
            var record = MakeRecord();
            await _store.SaveAsync(record);

            await _store.MarkAsReplayedAsync(record.Id);

            var updated = await _store.GetAsync(record.Id);
            Assert.That(updated, Is.Not.Null);
            Assert.That(updated!.Replayed, Is.True);
            Assert.That(updated.ReplayedAt, Is.Not.Null);
        }

        // ── DeleteOlderThanAsync ──────────────────────────────────────────────────

        [Test]
        public async Task DeleteOlderThanAsync_RemovesOnlyRecordsBeforeCutoff()
        {
            var old = MakeRecord(deadLetteredAt: DateTime.UtcNow.AddDays(-10));
            var recent = MakeRecord(deadLetteredAt: DateTime.UtcNow);

            await _store.SaveAsync(old);
            await _store.SaveAsync(recent);

            var cutoff = DateTime.UtcNow.AddDays(-1);
            await _store.DeleteOlderThanAsync(cutoff);

            var oldResult = await _store.GetAsync(old.Id);
            var recentResult = await _store.GetAsync(recent.Id);

            Assert.That(oldResult, Is.Null, "Old record should have been deleted");
            Assert.That(recentResult, Is.Not.Null, "Recent record should remain");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static DeadLetterRecord MakeRecord(
            string? messageType = null,
            string? reason = null,
            string? cloudProvider = null,
            DateTime? deadLetteredAt = null)
        {
            return new DeadLetterRecord
            {
                Id = Guid.NewGuid().ToString(),
                MessageId = Guid.NewGuid().ToString(),
                Body = "{}",
                MessageType = messageType ?? "TestMessage",
                Reason = reason ?? "TestReason",
                CloudProvider = cloudProvider ?? "aws",
                OriginalSource = "test-queue",
                DeadLetterSource = "test-dlq",
                DeadLetteredAt = deadLetteredAt ?? DateTime.UtcNow,
                DeliveryCount = 3,
                Replayed = false
            };
        }
    }
}
