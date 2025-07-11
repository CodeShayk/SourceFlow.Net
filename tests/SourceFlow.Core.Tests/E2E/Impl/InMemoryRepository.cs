using System.Collections.Concurrent;

namespace SourceFlow.Core.Tests.E2E.Impl
{
    public class InMemoryRepository : IDomainRepository
    {
        private readonly ConcurrentDictionary<int, IEntity> _cache = new();

        public Task DeleteAsync<TEntity>(TEntity entity) where TEntity : IEntity
        {
            if (entity?.Id == null)
                throw new ArgumentNullException(nameof(entity));

            _cache.TryRemove(entity.Id, out _);

            return Task.CompletedTask;
        }

        public Task<TEntity> GetByIdAsync<TEntity>(int id) where TEntity : class, IEntity
        {
            if (id == 0)
                throw new ArgumentNullException(nameof(id));

            var success = _cache.TryGetValue(id, out var entity);

            return Task.FromResult(success ? (TEntity)entity : null);
        }

        public Task PersistAsync<TEntity>(TEntity entity) where TEntity : IEntity
        {
            if (entity?.Id == null)
                throw new ArgumentNullException(nameof(entity));

            if (entity.Id == 0)
                entity.Id = new Random().Next();

            _cache[entity.Id] = entity;

            return Task.CompletedTask;
        }
    }
}