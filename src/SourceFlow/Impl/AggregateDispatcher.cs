using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Aggregate;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Bus;

namespace SourceFlow.Impl
{
    /// <summary>
    /// This dispatcher is responsible for dispatching events to the appropriate subscribing aggregates.
    /// </summary>
    internal class AggregateDispatcher : IEventDispatcher
    {
        /// <summary>
        /// Logger for the event queue to log events and errors.
        /// </summary>
        private readonly ILogger<IEventDispatcher> logger;

        /// <summary>
        /// Represents a collection of aggregate root objects.
        /// </summary>
        /// <remarks>This field holds a read-only collection of objects that implement the <see cref="IAggregate"/>
        /// interface. It is intended to be used internally to manage or process aggregate roots within the context of the
        /// application.</remarks>
        private readonly IEnumerable<IAggregate> aggregates;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventDispatcher"/> class with the specified aggregates and view projections.
        /// </summary>
        /// <param name="aggregates"></param>
        /// <param name="logger"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public AggregateDispatcher(IEnumerable<IAggregate> aggregates, ILogger<IEventDispatcher> logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.aggregates = aggregates ?? throw new ArgumentNullException(nameof(aggregates));
        }

        /// <summary>
        /// Dequeues the event to all aggregates that can handle it.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        private async Task Dispatch<TEvent>(TEvent @event)
            where TEvent : IEvent
        {
            var tasks = new List<Task>();

            foreach (var aggregate in aggregates)
            {
                var handlerType = typeof(ISubscribes<>).MakeGenericType(@event.GetType());
                if (!handlerType.IsAssignableFrom(aggregate.GetType()))
                    continue;

                var method = typeof(ISubscribes<>)
                            .MakeGenericType(@event.GetType())
                            .GetMethod(nameof(ISubscribes<TEvent>.Handle));

                var task = (Task)method.Invoke(aggregate, new object[] { @event });

                tasks.Add(task);

                logger?.LogInformation("Action=Event_Disptcher_Aggregate, Event={Event}, Aggregate={Aggregate}, Handler:{Handler}",
                       @event.GetType().Name, aggregate.GetType().Name, method.Name);
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Dispatches the event to both aggregates and view projections.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="event"></param>
        public void Dispatch(object sender, IEvent @event)
        {
            Dispatch(@event).GetAwaiter().GetResult();
            logger?.LogInformation("Action=Event_Dispatcher_Complete, Event={Event}, Sender:{sender}",
                      @event.Name, sender.GetType().Name);
        }
    }
}