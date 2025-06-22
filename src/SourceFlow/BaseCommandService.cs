using System.Threading.Tasks;

namespace SourceFlow
{
    public class BaseCommandService
    {
        protected readonly IAggregateRootRepository aggregateRootRepository;
        protected readonly IAggregateRootFactory aggregateRootFactory;

        public BaseCommandService(IAggregateRootRepository aggregateRootRepository, IAggregateRootFactory aggregateRootFactory)
        {
            this.aggregateRootRepository = aggregateRootRepository;
            this.aggregateRootFactory = aggregateRootFactory;
        }

        protected Task<IAggregateRoot> GetAggregateRoot<TAggregateRoot>(int id) where TAggregateRoot : IAggregateRoot
        {
            return aggregateRootRepository.GetByIdAsync(new AggregateReference(id, typeof(TAggregateRoot)));
        }

        protected Task SaveAggregateRoot<TAggregateRoot>(TAggregateRoot aggregateRoot) where TAggregateRoot : IAggregateRoot
        {
            return aggregateRootRepository.SaveAsync(aggregateRoot);
        }

        protected async Task<TAggregateRoot> CreateAggregateRootAsync<TAggregateRoot>(IIdentity state = null) where TAggregateRoot : IAggregateRoot
        {
            return await aggregateRootFactory.CreateAsync<TAggregateRoot>(state);
        }
    }
}