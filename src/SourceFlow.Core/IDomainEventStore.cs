// ====================================================================================
// CORE EVENT SOURCING ABSTRACTIONS
// ====================================================================================

using System;
using System.Collections.Generic;

namespace SourceFlow.Core
{
    public interface IDomainEventStore
    {
        void Append(Guid aggregateId, IDomainEvent @event);
        IEnumerable<IDomainEvent> GetEvents(Guid aggregateId);
    }
}