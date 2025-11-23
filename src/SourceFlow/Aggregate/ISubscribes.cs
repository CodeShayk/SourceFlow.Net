using System.Threading.Tasks;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Aggregate
{
    /// <summary>
    /// Interface for subscribing to events in the event-driven aggregates.
    /// </summary>
    /// <typeparam name="TEvent"></typeparam>
    public interface ISubscribes<in TEvent>
        where TEvent : IEvent
    {
        /// <summary>
        /// Handles the subscribed event.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        Task Handle(TEvent @event);
    }
}