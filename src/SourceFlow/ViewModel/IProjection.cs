using System.Threading.Tasks;
using SourceFlow.Messaging;

namespace SourceFlow.ViewModel
{
    /// <summary>
    /// Interface for applying an event to view model for data projection.
    /// </summary>
    public interface IProjection
    {
    }

    /// <summary>
    /// Interface for applying an event to view model for data projection.
    /// </summary>
    /// <typeparam name="TEvent"></typeparam>
    public interface IProjectOn<TEvent> : IProjection
        where TEvent : IEvent
    {
        /// <summary>
        /// Applies the specified event to the view model.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        Task Apply(TEvent @event);
    }
}