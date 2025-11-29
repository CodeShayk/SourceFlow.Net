using System.Threading.Tasks;

namespace SourceFlow.Messaging.Commands
{
    /// <summary>
    /// Interface for subscribing commands in the event-driven architecture.
    /// </summary>
    public interface ICommandSubscriber
    {
        ///// <summary>
        ///// Registers a saga with the command bus.
        ///// </summary>
        ///// <param name="saga"></param>
        //void Register(ISaga saga);

        /// <summary>
        /// Subscribes a command
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="command"></param>
        /// <returns></returns>
        Task Subscribe<TCommand>(TCommand command)
            where TCommand : ICommand;
    }
}
