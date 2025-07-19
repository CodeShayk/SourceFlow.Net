using System.Threading.Tasks;
using SourceFlow.Messaging;

namespace SourceFlow.ViewModel
{
    /// <summary>
    /// Base class for implementing event transform for ETL processing.
    /// </summary>
    /// <typeparam name="TEvent"></typeparam>
    public abstract class BaseViewProjection<TEvent> : IViewProjection<TEvent>
        where TEvent : IEvent
    {
        /// <summary>
        /// Repository for managing view models.
        /// </summary>
        protected IViewProvider ViewRepository { get; set; }

        /// <summary>
        /// Apply the event to view model.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        public abstract Task Apply(TEvent @event);
    }
}