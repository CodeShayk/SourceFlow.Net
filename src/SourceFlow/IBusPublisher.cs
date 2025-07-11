using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for publishing events to subscribers in the event-driven architecture.
    /// </summary>
    public interface IBusPublisher
    {
        /// <summary>
        /// Publishes an event to all subscribers.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        Task Publish<TEvent>(TEvent @event)
              where TEvent : IEvent;
    }
}