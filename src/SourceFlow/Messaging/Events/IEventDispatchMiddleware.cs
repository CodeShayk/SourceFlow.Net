using System;
using System.Threading.Tasks;

namespace SourceFlow.Messaging.Events
{
    /// <summary>
    /// Defines middleware that can intercept event dispatch operations in the event queue pipeline.
    /// </summary>
    public interface IEventDispatchMiddleware
    {
        /// <summary>
        /// Invokes the middleware logic for an event dispatch operation.
        /// </summary>
        /// <typeparam name="TEvent">The type of event being dispatched.</typeparam>
        /// <param name="event">The event being dispatched.</param>
        /// <param name="next">A delegate to invoke the next middleware or the core dispatch logic.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task InvokeAsync<TEvent>(TEvent @event, Func<TEvent, Task> next) where TEvent : IEvent;
    }
}
