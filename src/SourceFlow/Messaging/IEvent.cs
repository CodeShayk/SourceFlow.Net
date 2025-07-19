using SourceFlow.Aggregate;

namespace SourceFlow.Messaging
{
    public interface IEvent : IName, IMetadata

    {
        /// <summary>
        /// Gets or sets the payload of the event, which is an entity that contains the data associated with the event.
        /// </summary>
        IEntity Payload { get; set; }
    }
}