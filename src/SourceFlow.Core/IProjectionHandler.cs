// ====================================================================================
// CORE EVENT SOURCING ABSTRACTIONS
// ====================================================================================

using System.Threading.Tasks;

namespace SourceFlow.Core
{
    public interface IProjectionHandler<T> where T : IEvent
    {
        Task HandleAsync(T @event);
    }
}