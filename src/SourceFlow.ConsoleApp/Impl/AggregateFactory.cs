using Microsoft.Extensions.DependencyInjection;

namespace SourceFlow.ConsoleApp.Impl
{
    public class AggregateFactory : IAggregateFactory
    {
        private readonly IServiceProvider serviceProvider;

        public AggregateFactory(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public async Task<TAggregateRoot> CreateAsync<TAggregateRoot>()
            where TAggregateRoot : IAggregateRoot
        {
            // Resolve the aggregate root from the container
            var aggregate = serviceProvider.GetService<IAggregateRoot>();
            return await Task.FromResult((TAggregateRoot)aggregate);
        }
    }
}