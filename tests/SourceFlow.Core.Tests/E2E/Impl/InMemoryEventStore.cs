using System.Collections.Concurrent;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Core.Tests.E2E.Impl
{
    public class InMemoryEventStore : ICommandStore
    {
        private readonly ConcurrentDictionary<int, List<CommandData>> _store = new();

        public Task Append(CommandData command)
        {
            if (!_store.ContainsKey(command.EntityId))
                _store[command.EntityId] = new List<CommandData>();

            _store[command.EntityId].Add(command);

            return Task.CompletedTask;
        }

        
        public async Task<IEnumerable<CommandData>> Load(int entityId)
        {
            return await Task.FromResult(_store.TryGetValue(entityId, out var events)
               ? events
               : Enumerable.Empty<CommandData>());
        }
    }
}