using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for handling events in the event-driven saga.
    /// </summary>
    public interface ISagaHandler
    {
        /// <summary>
        /// Checks if the saga can handle the specified event.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        Task<bool> CanHandleEvent<TEvent>(TEvent @event)
            where TEvent : IEvent;

        /// <summary>
        /// Handles the specified event asynchronously in the saga.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        Task HandleAsync<TEvent>(TEvent @event)
            where TEvent : IEvent;
    }
}