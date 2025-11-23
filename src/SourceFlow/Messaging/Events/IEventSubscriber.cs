using System.Threading.Tasks;

namespace SourceFlow.Messaging.Events
{
    /// <summary>
    /// Defines a contract for subscribing to events within the event-driven architecture.
    /// </summary>
    public interface IEventSubscriber
    {
        /// <summary>
        /// Subscribes to the specified event.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        Task Subscribe<TEvent>(TEvent @event) where TEvent : IEvent;
    }
}
