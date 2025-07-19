using SourceFlow.Aggregate;

namespace SourceFlow.Messaging
{
    /// <summary>
    /// Base class for implementing events in an event-driven architecture.
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    public abstract class Event<TEntity> : IEvent
        where TEntity : IEntity
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Event{TEntity}"/> class with a specified payload.
        /// </summary>
        /// <param name="payload"></param>
        public Event(TEntity payload)
        {
            Metadata = new Metadata();
            Name = GetType().Name;
            Payload = payload;
        }

        /// <summary>
        /// Metadata associated with the event, which includes information such as event ID, occurrence time, and sequence number.
        /// </summary>
        public Metadata Metadata { get; set; }

        /// <summary>
        /// Name of the event, typically the class name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Payload of the event, representing the entity that the event is about.
        /// </summary>
        public TEntity Payload { get; set; }

        /// <summary>
        /// Payload of the event, representing the entity that the event is about.
        /// </summary>
        IEntity IEvent.Payload
        {
            get { return Payload; }
            set
            {
                Payload = (TEntity)value;
            }
        }
    }
}