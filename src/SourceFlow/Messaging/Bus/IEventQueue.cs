using System;
using System.Threading.Tasks;

namespace SourceFlow.Messaging.Bus
{
    public interface IEventQueue
    {
        /// <summary>
        /// Handlers that are invoked to dispatch an event that is dequeued from the event queue.
        /// </summary>
        event EventHandler<IEvent> Handlers;

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