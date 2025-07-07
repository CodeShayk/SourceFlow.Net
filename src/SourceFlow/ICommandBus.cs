using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for the command bus in the event-driven architecture.
    /// </summary>
    public interface ICommandBus
    {
        /// <summary>
        /// Publishes an event to all subscribed sagas.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        Task PublishAsync<TEvent>(TEvent @event)
             where TEvent : IEvent;

        /// <summary>
        /// Replays all events for a given aggregate.
        /// </summary>
        /// <param name="aggregateId">Unique aggregate entity id.</param>
        /// <returns></returns>
        Task ReplayEvents(int aggregateId);

        /// <summary>
        /// Registers a saga with the command bus.
        /// </summary>
        /// <param name="saga"></param>
        void RegisterSaga(ISaga saga);

        /// <summary>
        /// Registers a data view with the command bus.
        /// </summary>
        /// <param name="view"></param>
        void RegisterView(IDataView view);
    }
}