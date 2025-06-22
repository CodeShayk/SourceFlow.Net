// ====================================================================================
// CORE EVENT SOURCING ABSTRACTIONS
// ====================================================================================

using System.Collections.Generic;

namespace SourceFlow.Core
{
    public interface IAggregate
    {
        string Id { get; set; }
        int Version { get; set; }
    }
}