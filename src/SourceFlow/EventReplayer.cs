using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for replaying events in the event-driven architecture.
    /// </summary>
    public class EventReplayer : IEventReplayer
    {
        /// <summary>
        /// The command bus used to replay events for a given aggregate.
        /// </summary>
        private readonly ICommandBus commandBus;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventReplayer"/> class.
        /// </summary>
        /// <param name="commandBus"></param>
        public EventReplayer(ICommandBus commandBus)
        {
            this.commandBus = commandBus;
        }

        /// <summary>
        /// Replays event stream for a given aggregate.
        /// </summary>
        /// <param name="aggregateId"></param>
        /// <returns></returns>
        async Task IEventReplayer.ReplayEventsAsync(Guid aggregateId)
        {
            await commandBus.ReplayEvents(aggregateId);
        }
    }
}