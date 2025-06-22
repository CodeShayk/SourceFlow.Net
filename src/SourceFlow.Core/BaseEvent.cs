// ====================================================================================
// CORE EVENT SOURCING ABSTRACTIONS
// ====================================================================================

using System;

namespace SourceFlow.Core
{
    // ====================================================================================
    // BASE EVENT IMPLEMENTATION
    // ====================================================================================

    public abstract class BaseEvent : IEvent
    {
        public Guid EventId { get; private set; } = Guid.NewGuid();
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public abstract string EventType { get; }
        public int Version { get; private set; } = 1;
    }
}