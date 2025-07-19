using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for the event store in the event-driven architecture.
    /// </summary>
    public interface IEventStore
    {
        /// <summary>
        /// Appends an event to the event store.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        Task Append(ICommand @event);

        /// <summary>
        /// Loads all events for a given aggregate from the event store.
        /// </summary>
        /// <param name="aggregateId">Unique aggregate entity id.</param>
        /// <returns></returns>
        Task<IEnumerable<ICommand>> Load(int aggregateId);

        /// <summary>
        /// Gets the next sequence number for an event.
        /// </summary>
        /// <param name="aggregateId">Unique aggregate entity id.</param>
        /// <returns></returns>
        Task<int> GetNextSequenceNo(int aggregateId);
    }
}