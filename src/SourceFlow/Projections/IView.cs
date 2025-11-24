using System.Threading.Tasks;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Projections
{
    /// <summary>
    /// Interface for applying an event to view model for data projection.
    /// </summary>
    public interface IView
    {
        Task Apply<TEvent>(TEvent @event)
           where TEvent : IEvent;
    }
}