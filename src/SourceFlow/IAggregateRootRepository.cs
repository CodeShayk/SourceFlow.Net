using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IAggregateRootRepository
    {
        Task<IAggregateRoot> GetByIdAsync(AggregateReference aggregateRoot);

        Task SaveAsync(IAggregateRoot aggregateRoot);
    }
}