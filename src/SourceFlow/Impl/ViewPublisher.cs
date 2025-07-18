using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Impl
{
    /// <summary>
    /// Implementation of the IViewPublisher interface for publishing events to an View model transformations.
    /// </summary>
    internal class ViewPublisher : IViewPublisher
    {
        /// <summary>
        /// Collection of view model transforms that will be applied to the events to transform to view models upon publishing.
        /// </summary>
        private IEnumerable<IViewTransform> transforms;

        /// <summary>
        /// Logger for the ETL publisher to log events and errors.
        /// </summary>
        private ILogger logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ViewPublisher"/> class.
        /// </summary>
        /// <param name="eventTransforms"></param>
        public ViewPublisher(IEnumerable<IViewTransform> transforms, ILogger<ViewPublisher> logger)
        {
            this.transforms = transforms;
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Publishes an event to the ETL process asynchronously, applying all registered transforms.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        public async Task Publish<TEvent>(TEvent @event)
            where TEvent : IEvent
        {
            var tasks = new List<Task>();

            foreach (var transform in transforms)
                if (CanHandle(transform, @event.GetType()))
                {
                    var method = typeof(IViewTransform<>)
                           .MakeGenericType(@event.GetType())
                           .GetMethod(nameof(IViewTransform<TEvent>.Transform));

                    var task = (Task)method.Invoke(transform, new object[] { @event });

                    logger?.LogInformation("Action=View_Transforms, Event={Event}, Aggregate={Aggregate}, SequenceNo={No}, Transform:{Transform}",
                            @event.GetType().Name, @event.Entity.Type.Name, @event.SequenceNo, transform.GetType().Name);

                    tasks.Add(task);
                }

            if (!tasks.Any())
                return;

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Checks if the given event handler is a generic event handler for the specified event type.
        /// </summary>
        /// <param name="instance"></param>
        /// <param name="eventType"></param>
        /// <returns></returns>
        private static bool CanHandle(IViewTransform instance, Type eventType)
        {
            if (instance == null || eventType == null)
                return false;

            var handlerType = typeof(IViewTransform<>).MakeGenericType(eventType);
            return handlerType.IsAssignableFrom(instance.GetType());
        }
    }
}