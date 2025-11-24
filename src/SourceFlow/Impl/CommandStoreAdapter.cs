using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Impl
{
    internal class CommandStoreAdapter : ICommandStoreAdapter
    {
        private readonly ICommandStore store;

        public CommandStoreAdapter(ICommandStore store) => this.store = store;

        public Task Append(ICommand command)
        {
            return store.Append(command);
        }

        public Task<IEnumerable<ICommand>> Load(int entityId)
        {
            return store.Load(entityId);
        }

        public async Task<int> GetNextSequenceNo(int entityId)
        {
            var events = await store.Load(entityId);

            if (events!=null && events.Any())
                return events.Max<ICommand, int>(c => ((IMetadata)c).Metadata.SequenceNo) + 1;

            return 1;
        }
    }
}