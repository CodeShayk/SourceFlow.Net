using System;
using System.Threading.Tasks;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Messaging.Bus
{
    /// <summary>
    /// Interface for the command bus in the event-driven architecture.
    /// </summary>
    public interface ICommandBus
    {
        /// <summary>
        /// Publishes a command to all subscribed sagas.
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        Task Publish<TCommand>(TCommand command)
             where TCommand : ICommand;

        /// <summary>
        /// Replays all commands for a given aggregate.
        /// </summary>
        /// <param name="aggregateId">Unique aggregate entity id.</param>
        /// <returns></returns>
        Task Replay(int aggregateId);
    }
}