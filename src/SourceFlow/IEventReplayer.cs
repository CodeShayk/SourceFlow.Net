using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for replaying events in the event-driven architecture.
    /// </summary>
    public interface IEventReplayer
    {
        /// <summary>
        /// Replays all events for a given aggregate.
        /// </summary>
        /// <param name="aggregateId"></param>
        /// <returns></returns>
        Task ReplayEventsAsync(Guid aggregateId);
    }
}