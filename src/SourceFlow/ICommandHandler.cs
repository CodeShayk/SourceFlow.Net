using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for handling command in the event-driven saga.
    /// </summary>
    /// <typeparam name="TCommand"></typeparam>
    public interface ICommandHandler<in TCommand>
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