using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Aggregate;

namespace SourceFlow.Core.Tests.E2E.Impl
{
    public class AggregateFactory : IAggregateFactory
    {
        private readonly IServiceProvider serviceProvider;

        public AggregateFactory(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public async Task<TAggregateRoot> Create<TAggregateRoot>()
            where TAggregateRoot : IAggregate
        {
            // Resolve the aggregate root from the container
            var aggregate = serviceProvider.GetService<IAggregate>();
            return await Task.FromResult((TAggregateRoot)aggregate);
        }
    }
}