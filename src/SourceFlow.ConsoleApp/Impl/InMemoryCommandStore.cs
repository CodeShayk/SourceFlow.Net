using System.Collections.Concurrent;
using System.Collections.Immutable;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.ConsoleApp.Impl
{
    /// <summary>
    /// Thread-safe in-memory implementation of ICommandStore using immutable collections.
    /// </summary>
    public class InMemoryCommandStore : ICommandStore
    {
        // Thread-safe: ImmutableList creates new instance on every Add
        private readonly ConcurrentDictionary<int, ImmutableList<ICommand>> _store = new();

        // Thread-safe: Atomic counter per entity for sequence number generation
        private readonly ConcurrentDictionary<int, int> _sequenceCounters = new();

        /// <summary>
        /// Appends a command to the store in a thread-safe manner.
        /// </summary>
        public Task Append(ICommand command)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            // AddOrUpdate is atomic - thread-safe
            _store.AddOrUpdate(
                command.Entity.Id,
                // Add: Create new list with this command
                _ => ImmutableList.Create(command),
                // Update: Add command to existing list (creates new immutable list)
                (_, existingList) => existingList.Add(command)
            );

            return Task.CompletedTask;
        }

        /// <summary>
        /// Loads all commands for the specified entity.
        /// </summary>
        public async Task<IEnumerable<ICommand>> Load(int entityId)
        {
            return await Task.FromResult(_store.TryGetValue(entityId, out var events)
               ? events.AsEnumerable()
               : Enumerable.Empty<ICommand>());
        }

        /// <summary>
        /// Gets the next sequence number for the specified entity in a thread-safe manner.
        /// </summary>
        public Task<int> GetNextSequenceNo(int entityId)
        {
            // AddOrUpdate is atomic - thread-safe increment
            var nextSeq = _sequenceCounters.AddOrUpdate(
                entityId,
                1,  // First sequence number for new entity
                (_, current) => current + 1  // Atomic increment for existing entity
            );

            return Task.FromResult(nextSeq);
        }
    }
}