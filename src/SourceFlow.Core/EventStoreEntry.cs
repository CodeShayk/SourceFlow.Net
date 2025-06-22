// ====================================================================================
// CORE EVENT SOURCING ABSTRACTIONS
// ====================================================================================

using System;

namespace SourceFlow.Core
{
    // Event Store Entry
    public class EventStoreEntry
    {
        public Guid EventId { get; set; }
        public string StreamId { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string EventData { get; set; } = string.Empty;
        public string Metadata { get; set; }
        public DateTime Timestamp { get; set; }
        public int Version { get; set; }
        public long SequenceNumber { get; set; }
    }
}