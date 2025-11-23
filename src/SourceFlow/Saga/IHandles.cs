using System.Threading.Tasks;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

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
        /// Handles the specified command against entity.
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="command"></param>
        /// <returns></returns>
        Task Handle(IEntity entity, TCommand command);
    }
}