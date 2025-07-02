using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public class EventReplayer : IEventReplayer
    {
        private readonly ICommandBus commandBus;

        public EventReplayer(ICommandBus commandBus)
        {
            this.commandBus = commandBus;
        }

        async Task IEventReplayer.ReplayEventsAsync(Guid aggregateId)
        {
            await commandBus.ReplayEvents(aggregateId);
        }
    }
}