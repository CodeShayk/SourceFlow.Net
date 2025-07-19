using SourceFlow.Aggregate;

namespace SourceFlow.Messaging
{
    /// <summary>
    /// Base class for implementing events in an event-driven architecture.
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    public abstract class BaseEvent<TEntity> : IEvent
        where TEntity : IEntity
    {
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

        /// <summary>
        /// Creates a new instance of the <see cref="BaseEvent{TEntity}"/> class with the specified payload.
        /// </summary>
        /// <param name="payload"></param>
        public BaseEvent(TEntity payload)
        {
            Name = GetType().Name;
            Payload = payload;
        }
    }
}