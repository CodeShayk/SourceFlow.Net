// ====================================================================================
// CORE EVENT SOURCING ABSTRACTIONS
// ====================================================================================

using System.Threading.Tasks;

namespace SourceFlow.Core
{
    // ====================================================================================
    // REPOSITORY PATTERN
    // ====================================================================================

    public interface IRepository<T> where T : AggregateRoot
    {
        Task<T> GetByIdAsync(string id);

        Task SaveAsync(T aggregate);
    }
}