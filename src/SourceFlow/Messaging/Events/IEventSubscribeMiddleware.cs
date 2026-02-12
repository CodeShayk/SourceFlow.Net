using System;
using System.Threading.Tasks;

namespace SourceFlow.Messaging.Events
{
    /// <summary>
    /// Defines middleware that can intercept event subscribe operations in the event subscriber pipeline.
    /// </summary>
    public interface IEventSubscribeMiddleware
    {
        /// <summary>
        /// Invokes the middleware logic for an event subscribe operation.
        /// </summary>
        /// <typeparam name="TEvent">The type of event being subscribed.</typeparam>
        /// <param name="event">The event being subscribed.</param>
        /// <param name="next">A delegate to invoke the next middleware or the core subscribe logic.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task InvokeAsync<TEvent>(TEvent @event, Func<TEvent, Task> next) where TEvent : IEvent;
    }
}
