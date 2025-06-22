// ====================================================================================
// CORE EVENT SOURCING ABSTRACTIONS
// ====================================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceFlow.Core
{
    // ====================================================================================
    // EVENT STORE IMPLEMENTATION
    // ====================================================================================

    public interface IEventStore
    {
        Task SaveEventsAsync(string streamId, IEnumerable<IEvent> events, int expectedVersion);

        Task<IEnumerable<IEvent>> GetEventsAsync(string streamId);

        Task<IEnumerable<IEvent>> GetEventsAsync(string streamId, int fromVersion);
    }
}