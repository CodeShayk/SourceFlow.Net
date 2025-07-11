using System;

namespace SourceFlow
{
    /// <summary>
    /// Represents a handler for a saga event.
    /// </summary>
    public class SagaHandler
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SagaHandler"/> class.
        /// </summary>
        public SagaHandler(Type eventType, IEventHandler handler)
        {
            EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// Gets the type of the event for this handler.
        /// </summary>
        public Type EventType { get; }

        /// <summary>
        /// Gets the event handler for this saga event.
        /// </summary>
        public IEventHandler Handler { get; }
    }
}