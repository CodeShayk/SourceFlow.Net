using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using SourceFlow.ConsoleApp.Impl;
using SourceFlow.Core.Tests.Impl;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Core.Tests.Impl
{
    [TestFixture]
    public class InMemoryCommandStoreThreadSafetyTests
    {
        [Test]
        public async Task Append_ConcurrentAccess_NoDataLoss()
        {
            // Arrange
            var store = new InMemoryCommandStore();
            var entityId = 1;
            var commandCount = 1000;
            var threadCount = 10;
            var commandsPerThread = commandCount / threadCount;

            // Act: Append commands concurrently from multiple threads
            var tasks = Enumerable.Range(0, threadCount)
                .Select(threadId => Task.Run(async () =>
                {
                    for (int i = 0; i < commandsPerThread; i++)
                    {
                        var command = new DummyCommand
                        {
                            Entity = new EntityRef { Id = entityId },
                            Metadata = new Metadata { SequenceNo = threadId * commandsPerThread + i }
                        };
                        await store.Append(command);
                    }
                }));

            await Task.WhenAll(tasks);

            // Assert: All commands persisted
            var commands = await store.Load(entityId);
            var commandList = commands.ToList();

            Assert.That(commandList.Count, Is.EqualTo(commandCount),
                "All commands should be persisted, none lost");
        }

        [Test]
        public async Task GetNextSequenceNo_ConcurrentAccess_UniqueSequenceNumbers()
        {
            // Arrange
            var store = new InMemoryCommandStore();
            var entityId = 1;
            var sequenceCount = 1000;
            var threadCount = 10;
            var sequencesPerThread = sequenceCount / threadCount;
            var sequenceNumbers = new System.Collections.Concurrent.ConcurrentBag<int>();

            // Act: Get sequence numbers concurrently
            var tasks = Enumerable.Range(0, threadCount)
                .Select(_ => Task.Run(async () =>
                {
                    for (int i = 0; i < sequencesPerThread; i++)
                    {
                        var seqNo = await store.GetNextSequenceNo(entityId);
                        sequenceNumbers.Add(seqNo);
                    }
                }));

            await Task.WhenAll(tasks);

            // Assert: All sequence numbers are unique
            var uniqueSequences = sequenceNumbers.Distinct().ToList();
            Assert.That(uniqueSequences.Count, Is.EqualTo(sequenceCount),
                "All sequence numbers should be unique");

            // Assert: Sequence numbers are in valid range
            Assert.That(uniqueSequences.Min(), Is.EqualTo(1),
                "Minimum sequence should be 1");
            Assert.That(uniqueSequences.Max(), Is.EqualTo(sequenceCount),
                "Maximum sequence should equal count");
        }

        [Test]
        public async Task Append_MultipleEntities_ConcurrentAccess_NoInterference()
        {
            // Arrange
            var store = new InMemoryCommandStore();
            var entityCount = 10;
            var commandsPerEntity = 100;

            // Act: Append to multiple entities concurrently
            var tasks = Enumerable.Range(1, entityCount)
                .Select(entityId => Task.Run(async () =>
                {
                    for (int i = 0; i < commandsPerEntity; i++)
                    {
                        var command = new DummyCommand
                        {
                            Entity = new EntityRef { Id = entityId },
                            Metadata = new Metadata { SequenceNo = i + 1 }
                        };
                        await store.Append(command);
                    }
                }));

            await Task.WhenAll(tasks);

            // Assert: Each entity has correct count
            for (int entityId = 1; entityId <= entityCount; entityId++)
            {
                var commands = await store.Load(entityId);
                Assert.That(commands.Count(), Is.EqualTo(commandsPerEntity),
                    $"Entity {entityId} should have {commandsPerEntity} commands");
            }
        }

        [Test]
        public async Task GetNextSequenceNo_AppendConcurrently_SequenceMatchesCommandCount()
        {
            // Arrange
            var store = new InMemoryCommandStore();
            var entityId = 1;
            var commandCount = 500;

            // Act: Get sequence and append concurrently
            var tasks = Enumerable.Range(0, commandCount)
                .Select(_ => Task.Run(async () =>
                {
                    var seqNo = await store.GetNextSequenceNo(entityId);
                    var command = new DummyCommand
                    {
                        Entity = new EntityRef { Id = entityId },
                        Metadata = new Metadata { SequenceNo = seqNo }
                    };
                    await store.Append(command);
                }));

            await Task.WhenAll(tasks);

            // Assert: Command count matches last sequence number
            var commands = await store.Load(entityId);
            var commandList = commands.ToList();

            Assert.That(commandList.Count, Is.EqualTo(commandCount));

            // Assert: All sequences are unique and sequential (may not be ordered)
            var sequences = commandList.Select(c => c.Metadata.SequenceNo).ToList();
            var uniqueSequences = sequences.Distinct().ToList();

            Assert.That(uniqueSequences.Count, Is.EqualTo(commandCount),
                "All commands should have unique sequence numbers");
            Assert.That(uniqueSequences.OrderBy(s => s).SequenceEqual(Enumerable.Range(1, commandCount)),
                "Sequences should be 1 through N with no gaps");
        }

        [Test]
        public async Task Append_HighConcurrency_StressTest()
        {
            // Arrange
            var store = new InMemoryCommandStore();
            var entityId = 1;
            var commandCount = 10000;
            var threadCount = 100;
            var commandsPerThread = commandCount / threadCount;

            // Act: Heavy concurrent load
            var tasks = Enumerable.Range(0, threadCount)
                .Select(threadId => Task.Run(async () =>
                {
                    for (int i = 0; i < commandsPerThread; i++)
                    {
                        var seqNo = await store.GetNextSequenceNo(entityId);
                        var command = new DummyCommand
                        {
                            Entity = new EntityRef { Id = entityId },
                            Metadata = new Metadata { SequenceNo = seqNo }
                        };
                        await store.Append(command);
                    }
                }));

            await Task.WhenAll(tasks);

            // Assert: No data loss, no corruption
            var commands = await store.Load(entityId);
            var commandList = commands.ToList();

            Assert.That(commandList.Count, Is.EqualTo(commandCount),
                "No commands should be lost under high concurrency");

            var sequences = commandList.Select(c => c.Metadata.SequenceNo).ToList();
            Assert.That(sequences.Distinct().Count(), Is.EqualTo(commandCount),
                "All sequence numbers should be unique");
        }

        [Test]
        public void Append_NullCommand_ThrowsArgumentNullException()
        {
            // Arrange
            var store = new InMemoryCommandStore();

            // Act & Assert
            Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await store.Append(null!));
        }
    }
}
