using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public abstract class BaseCommandService : ICommandService
    {
        protected readonly IAggregateRepository aggregateRepository;
        protected readonly IAggregateFactory aggregateFactory;

        public BaseCommandService(IAggregateRepository aggregateRepository, IAggregateFactory aggregateRootFactory)
        {
            this.aggregateRepository = aggregateRepository;
            this.aggregateFactory = aggregateRootFactory;
        }

        public async Task<TAggregateRoot> GetAggregate<TAggregateRoot>(Guid id) where TAggregateRoot : IAggregateRoot
        {
            var aggregateRoot = await aggregateRepository.GetByIdAsync(new AggregateReference(id, typeof(TAggregateRoot)));
            return (TAggregateRoot)aggregateRoot;
        }

        public Task SaveAggregate<TAggregateRoot>(TAggregateRoot aggregateRoot) where TAggregateRoot : IAggregateRoot
        {
            return aggregateRepository.SaveAsync(aggregateRoot);
        }

        public async Task<TAggregateRoot> InitializeAggregate<TAggregateRoot>(IIdentity state = null) where TAggregateRoot : IAggregateRoot
        {
            var aggregate = await aggregateFactory.CreateAsync<TAggregateRoot>(state);
            aggregate.State = state;
            return aggregate;
        }
    }
}