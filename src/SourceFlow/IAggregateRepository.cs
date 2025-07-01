using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IAggregateRepository
    {
        Task<IAggregateRoot> GetByIdAsync(AggregateReference aggregateRoot);

        Task SaveAsync(IAggregateRoot aggregateRoot);
    }
}