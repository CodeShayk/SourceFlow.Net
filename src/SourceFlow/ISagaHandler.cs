using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for handling events in the event-driven saga.
    /// </summary>
    /// <typeparam name="TEvent"></typeparam>
    public interface ISagaHandler<in TEvent>
        where TEvent : IEvent
    {
        /// <summary>
        /// Handles the specified event.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        Task Handle(TEvent @event);
    }
}