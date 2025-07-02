using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public class BusReplayer : IBusReplayer
    {
        private readonly ICommandBus commandBus;

        public BusReplayer(ICommandBus commandBus)
        {
            this.commandBus = commandBus;
        }

        async Task IBusReplayer.ReplayEventsAsync(Guid aggregateId)
        {
            await commandBus.ReplayEvents(aggregateId);
        }
    }
}