using System.Threading.Tasks;

namespace SourceFlow.Messaging.Bus
{
    /// <summary>
    /// Interface for replaying commands in the event-driven architecture.
    /// </summary>
    public interface ICommandReplayer
    {
        /// <summary>
        /// Replays all commands for a given aggregate.
        /// </summary>
        /// <param name="aggregateId">Unique aggregate entity id.</param>
        /// <returns></returns>
        Task Replay(int aggregateId);
    }
}