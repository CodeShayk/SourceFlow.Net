using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for replaying events in the event-driven architecture.
    /// </summary>
    internal class EventReplayer : IEventReplayer
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
        /// <param name="aggregateId">Unique aggregate entity id.</param>
        /// <returns></returns>
        async Task IEventReplayer.ReplayEvents(int aggregateId)
        {
            await commandBus.ReplayEvents(aggregateId);
        }
    }
}