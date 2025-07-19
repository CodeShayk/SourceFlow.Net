using System.Threading.Tasks;
using SourceFlow.Messaging;

namespace SourceFlow.Saga
{
    /// <summary>
    /// Interface for handling command in the event-driven saga.
    /// </summary>
    /// <typeparam name="TCommand"></typeparam>
    public interface IHandles<in TCommand>
        where TCommand : ICommand
    {
        /// <summary>
        /// Handles the specified command.
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        Task Handle(TCommand command);
    }
}