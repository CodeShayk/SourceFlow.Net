using SourceFlow.Aggregate;

namespace SourceFlow.Messaging
{
    /// <summary>
    /// Represents an event in the event-driven architecture.
    /// </summary>
    public interface IEvent
    {
        /// <summary>
        /// Gets the name for the event.
        /// </summary>
        string Name { get; set; }

        /// <summary>
        /// Gets the payload of the event, which is an entity that contains the data associated with the event.
        /// </summary>
        IEntity Payload { get; set; }
    }
}