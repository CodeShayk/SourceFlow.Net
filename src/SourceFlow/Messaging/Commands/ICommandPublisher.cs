using System.Threading.Tasks;

namespace SourceFlow.Messaging.Commands
{
    /// <summary>
    /// Interface for publishing commands to bus in the event-driven architecture.
    /// </summary>
    public interface ICommandPublisher
    {
        /// <summary>
        /// Publishes a command to command bus.
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        Task Publish<TCommand>(TCommand command)
            where TCommand : ICommand;

        /// <summary>
        /// Replays commands for the specified entity Id.
        /// </summary>
        /// <param name="entityId"></param>
        /// <returns></returns>
        Task ReplayCommands(int entityId);
    }
}
