using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Implementation of the IETLPublisher interface for publishing events to an ETL process.
    /// </summary>
    public class ETLPublisher : IETLPublisher
    {
        /// <summary>
        /// Collection of view model transforms that will be applied to the events to transform to view models upon publishing.
        /// </summary>
        private IEnumerable<IViewModelTransform> transforms;

        /// <summary>
        /// Initializes a new instance of the <see cref="ETLPublisher"/> class.
        /// </summary>
        /// <param name="eventTransforms"></param>
        public ETLPublisher(IEnumerable<IViewModelTransform> transforms)
        {
            this.transforms = transforms;
        }

        /// <summary>
        /// Publishes an event to the ETL process asynchronously, applying all registered transforms.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        public async Task PublishAsync(IEvent @event)
        {
            foreach (var transform in transforms)
            {
                if (typeof(IViewModelTransform<IEvent>).IsAssignableFrom(transform.GetType()))
                {
                    await ((IViewModelTransform<IEvent>)transform).TransformAsync(@event);
                }
            }
        }
    }
}