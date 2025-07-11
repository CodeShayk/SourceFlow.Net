using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace SourceFlow
{
    /// <summary>
    /// Factory for creating aggregate roots in the event-driven architecture.
    /// </summary>
    public class AggregateFactory : IAggregateFactory
    {
        /// <summary>
        /// Service provider for resolving dependencies.
        /// </summary>
        private readonly IServiceProvider serviceProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="AggregateFactory"/> class.
        /// </summary>
        /// <param name="serviceProvider"></param>
        public AggregateFactory(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Creates a singleton instance of an aggregate root with the specified state.
        /// </summary>
        /// <typeparam name="TAggregateRoot"></typeparam>
        /// <returns></returns>
        public async Task<TAggregateRoot> CreateAsync<TAggregateRoot>()
            where TAggregateRoot : IAggregateRoot
        {
            // Resolve the aggregate root from the container
            var aggregate = serviceProvider.GetService<IAggregateRoot>();
            return await Task.FromResult((TAggregateRoot)aggregate);
        }
    }
}