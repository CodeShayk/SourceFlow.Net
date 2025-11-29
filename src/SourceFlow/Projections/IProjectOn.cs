using System.Threading.Tasks;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Projections
{
    /// <summary>
    /// Interface for applying an event to view model for data projection.
    /// </summary>
    /// <typeparam name="TEvent"></typeparam>
    public interface IProjectOn<TEvent> 
        where TEvent : IEvent
    {
        /// <summary>
        /// Applies the specified event to the view model.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        Task<IViewModel> On(TEvent @event);
    }
}