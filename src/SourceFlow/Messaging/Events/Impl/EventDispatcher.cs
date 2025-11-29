using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SourceFlow.Observability;
using SourceFlow.Performance;

namespace SourceFlow.Messaging.Events.Impl
{
    internal class EventDispatcher : IEventDispatcher
    {
        /// <summary>
        /// Represents a collection of subscribers interested in the event.
        /// </summary>
        /// <remarks>This collection contains instances of objects implementing the <see cref="IEventSubscriber"/> interface. Each subscribers in the collection subscribes to events of interest.</remarks>
        private IEnumerable<IEventSubscriber> subscribers;

        /// <summary>
        /// Logger for the event queue to log events and errors.
        /// </summary>
        private readonly ILogger<IEventDispatcher> logger;

        /// <summary>
        /// Telemetry service for observability.
        /// </summary>
        private readonly IDomainTelemetryService telemetry;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventDispatcher"/> class with the specified subscribers and logger.
        /// </summary>
        /// <param name="subscribers"></param>
        /// <param name="logger"></param>
        /// <param name="telemetry"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public EventDispatcher(IEnumerable<IEventSubscriber> subscribers, ILogger<IEventDispatcher> logger, IDomainTelemetryService telemetry)
        {
            this.subscribers = subscribers ?? throw new ArgumentNullException(nameof(subscribers));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        }

        /// <summary>
        /// Dispatch the event to all subscribers that can handle it.
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        /// <param name="event"></param>
        /// <returns></returns>
        public Task Dispatch<TEvent>(TEvent @event) where TEvent : IEvent
        {
            return telemetry.TraceAsync(
                "sourceflow.eventdispatcher.dispatch",
                async () =>
                {
                    // Use ArrayPool-based optimization for task collection
                    await TaskBufferPool.ExecuteAsync(
                        subscribers,
                        subscriber =>
                        {
                            logger?.LogInformation("Action=Event_Dispatcher, Event={Event}, Subscriber:{subscriber}",
                                    @event.Name, subscribers.GetType().Name);

                            return subscriber.Subscribe(@event);
                        });
                },
                activity =>
                {
                    activity?.SetTag("event.type", @event.GetType().Name);
                    activity?.SetTag("event.name", @event.Name);
                    activity?.SetTag("subscribers.count", subscribers.Count());
                });
        }
    }
}
