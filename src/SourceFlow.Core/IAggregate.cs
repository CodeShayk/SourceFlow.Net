// ====================================================================================
// CORE EVENT SOURCING ABSTRACTIONS
// ====================================================================================

namespace SourceFlow.Core
{
    public interface IAggregate
    {
        string Id { get; set; }
        int Version { get; set; }
    }
}