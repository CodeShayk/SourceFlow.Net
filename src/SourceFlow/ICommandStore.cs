using System.Collections.Generic;
using System.Threading.Tasks;
using SourceFlow.Messaging.Commands;

namespace SourceFlow
{
    /// <summary>
    /// Interface for the command store in the event-driven architecture.
    /// </summary>
    public interface ICommandStore
    {
        /// <summary>
        /// Appends a command to the store. Commands serve as units of auditable change in the event-driven architecture,
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        Task Append(ICommand command);

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