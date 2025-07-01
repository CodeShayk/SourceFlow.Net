using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public interface ICommandService
    {
        Task<TAggregateRoot> InitializeAggregate<TAggregateRoot>(IIdentity state = null) where TAggregateRoot : IAggregateRoot;

        Task<TAggregateRoot> GetAggregate<TAggregateRoot>(Guid id) where TAggregateRoot : IAggregateRoot;

        Task SaveAggregate<TAggregateRoot>(TAggregateRoot aggregateRoot) where TAggregateRoot : IAggregateRoot;
    }
}