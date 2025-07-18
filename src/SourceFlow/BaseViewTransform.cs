using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Base class for implementing event transform for ETL processing.
    /// </summary>
    /// <typeparam name="TEvent"></typeparam>
    public abstract class BaseViewTransform<TEvent> : IViewTransform<TEvent>
        where TEvent : IEvent
    {
        /// <summary>
        /// Repository for managing view models.
        /// </summary>
        protected IViewModelRepository ViewModelRepository { get; set; }

        /// <summary>
        /// Transform the event to view model.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        public abstract Task Transform(TEvent @event);
    }
}