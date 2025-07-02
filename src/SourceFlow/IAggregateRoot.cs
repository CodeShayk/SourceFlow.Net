using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for an aggregate root in the event-driven architecture.
    /// </summary>
    public interface IAggregateRoot
    {
        /// <summary>
        /// Applies an event to the aggregate root, updating its state accordingly.
        /// </summary>
        /// <param name="event"></param>
        /// <returns></returns>
        Task ApplyAsync(IEvent @event);

        /// <summary>
        /// Gets or sets the state for the aggregate root.
        /// </summary>
        IIdentity State { get; set; }
    }
}