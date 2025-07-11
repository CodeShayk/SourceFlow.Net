using Microsoft.Extensions.DependencyInjection;

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
            where TAggregateRoot : IAggregateRoot
        {
            // Resolve the aggregate root from the container
            var aggregate = serviceProvider.GetService<IAggregateRoot>();
            return await Task.FromResult((TAggregateRoot)aggregate);
        }
    }
}