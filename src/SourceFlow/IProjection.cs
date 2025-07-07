using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for a projection that transforms events into a data view.
    /// </summary>
    public interface IProjection
    {
    }

    /// <summary>
    /// Interface for a projection that transforms events into a data view.
    /// </summary>
    /// <typeparam name="TEvent"></typeparam>
    public interface IProjection<in TEvent> : IProjection where TEvent : IEvent
    {
        /// <summary>
        /// Projects an event to transform to a data view.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        Task ProjectAsync(TEvent @event);
    }
}