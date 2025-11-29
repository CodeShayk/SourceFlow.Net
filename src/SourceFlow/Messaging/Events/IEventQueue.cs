using System.Threading.Tasks;

namespace SourceFlow.Messaging.Events
{
    public interface IEventQueue
    {
        /// <summary>
        /// Enqueues an event in order to publish to subcribers.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        Task Enqueue<TEvent>(TEvent @event)
            where TEvent : IEvent;
    }
}
