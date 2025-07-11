using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for publishing events to an ETL (Extract, Transform, Load) process.
    /// </summary>
    public interface IETLPublisher
    {
        /// <summary>
        /// Publishes an event to the ETL process asynchronously.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        Task Publish<TEvent>(TEvent @event)
            where TEvent : IEvent;
    }
}