using System.Collections.Concurrent;

namespace SourceFlow.Core.Tests.E2E.Impl
{
    public class InMemoryRepository : IRepository
    {
        private readonly ConcurrentDictionary<int, IEntity> _cache = new();

        public Task Delete<TEntity>(TEntity entity) where TEntity : IEntity
        {
            if (entity?.Id == null)
                throw new ArgumentNullException(nameof(entity));

            _cache.TryRemove(entity.Id, out _);

            return Task.CompletedTask;
        }

        public Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity
        {
            if (id == 0)
                throw new ArgumentNullException(nameof(id));

            var success = _cache.TryGetValue(id, out var entity);

            if (!success || entity == null)
                throw new InvalidOperationException($"Entity not found for ID: {id}");

            return Task.FromResult((TEntity)entity);
        }

        public Task Persist<TEntity>(TEntity entity) where TEntity : IEntity
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