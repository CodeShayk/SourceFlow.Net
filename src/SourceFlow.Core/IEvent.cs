// ====================================================================================
// CORE EVENT SOURCING ABSTRACTIONS
// ====================================================================================

using System;

namespace SourceFlow.Core
{
    // ====================================================================================
    // BASE EVENT INTERFACE
    // ====================================================================================

    public interface IEvent
    {
        Guid EventId { get; }
        DateTime Timestamp { get; }
        string EventType { get; }
        int Version { get; }
    };
}