using System;
using System.Threading.Tasks;

namespace SourceFlow.Messaging.Bus
{
    public interface IEventQueue
    {
        /// <summary>
        /// Dispatchers that are invoked to publish an event that is dequeued from the event queue.
        /// </summary>
        event EventHandler<IEvent> Dispatchers;

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