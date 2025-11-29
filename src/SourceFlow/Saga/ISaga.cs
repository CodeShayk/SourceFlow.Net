using System.Threading.Tasks;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Saga
{
    /// <summary>
    /// Interface for a saga that handles events related to a specific aggregate root.
    /// </summary>
    /// <typeparam name="TSagaData">Data projected by the saga.</typeparam>
    public interface ISaga<TSagaData> : ISaga
        where TSagaData : IEntity
    {
    }

    /// <summary>
    /// Interface for handling events in the event-driven saga.
    /// </summary>
    public interface ISaga
    {
        /// <summary>
        /// Handles the specified command asynchronously in the saga.
        /// </summary>
        /// <typeparam name="TCommand"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        Task Handle<TCommand>(TCommand command)
            where TCommand : ICommand;
    }
}
