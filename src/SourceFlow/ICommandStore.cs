using System.Collections.Generic;
using System.Threading.Tasks;
using SourceFlow.Messaging.Commands;

namespace SourceFlow
{
    /// <summary>
    /// Interface for the command store in the event-driven architecture.
    /// Stores work with serialized CommandData for persistence.
    /// </summary>
    public interface ICommandStore
    {
        /// <summary>
        /// Appends serialized command data to the store.
        /// </summary>
        /// <param name="commandData">Serialized command data</param>
        /// <returns></returns>
        Task Append(CommandData commandData);

        /// <summary>
        /// Loads all serialized command data for a given aggregate from the store.
        /// </summary>
        /// <param name="aggregateId">Unique aggregate entity id.</param>
        /// <returns>Collection of serialized command data</returns>
        Task<IEnumerable<CommandData>> Load(int aggregateId);
    }
}
