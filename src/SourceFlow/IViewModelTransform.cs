using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for transforming an event to view model in the ETL processing.
    /// </summary>
    public interface IViewModelTransform
    {
    }

    /// <summary>
    /// Interface for transforming an event to view model in the ETL processing.
    /// </summary>
    /// <typeparam name="TEvent"></typeparam>
    public interface IViewModelTransform<TEvent> : IViewModelTransform
        where TEvent : IEvent
    {
        /// <summary>
        /// Transforms the event for ETL processing.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        Task TransformAsync(TEvent @event);
    }
}