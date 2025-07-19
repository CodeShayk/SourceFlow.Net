using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for publishing events to an View Model Transforms.
    /// </summary>
    internal interface IViewPublisher
    {
        /// <summary>
        /// Publishes an event to the View ETL process asynchronously.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        Task Publish<TEvent>(TEvent @event)
            where TEvent : IEvent;
    }
}