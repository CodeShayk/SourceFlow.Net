using System.Collections.Concurrent;

namespace SourceFlow.ConsoleApp.Impl
{
    public class AccountRepository : IAggregateRepository
    {
        private readonly ConcurrentDictionary<Guid, IAggregateRoot> _cache = new();

        public Task<IAggregateRoot> GetByIdAsync(AggregateReference aggregateRoot)
        {
            if (aggregateRoot == null)
                throw new ArgumentNullException(nameof(aggregateRoot));

            if (_cache.TryGetValue(aggregateRoot.Id, out var existingAggregate))
            {
                return Task.FromResult(existingAggregate);
            }

            return Task.FromResult<IAggregateRoot>(null);
        }

        public Task SaveAsync(IAggregateRoot aggregateRoot)
        {
            if (aggregateRoot?.State == null)
                throw new ArgumentNullException(nameof(aggregateRoot));

            _cache[aggregateRoot.State.Id] = aggregateRoot;

            return Task.CompletedTask;
        }
    }
}