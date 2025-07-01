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

        public async Task<TAggregateRoot> CreateAsync<TAggregateRoot>(IIdentity state = null)
            where TAggregateRoot : IAggregateRoot
        {
            // Resolve the aggregate root from the container
            var aggregate = serviceProvider.GetService<IAggregateRoot>();
            if (aggregate == null)
            {
                throw new InvalidOperationException("No aggregate roots registered in the service provider.");
            }
            // var aggregate = (TAggregateRoot)aggregates.Where(a => a.GetType() == typeof(TAggregateRoot)).FirstOrDefault();
            // Optionally, you can initialize the aggregate with 'state' if needed
            if (state != null)
            {
                // Assuming TAggregateRoot has a constructor or method to set the state
                // This is just an example; actual implementation may vary
                aggregate.State = state;
            }

            return await Task.FromResult((TAggregateRoot)aggregate);
        }
    }
}