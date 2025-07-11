using System.Collections.Concurrent;

namespace SourceFlow.ConsoleApp.Impl
{
    public class InMemoryViewModelRepository : IViewModelRepository
    {
        private readonly ConcurrentDictionary<int, IViewModel> _cache = new();

        public Task DeleteAsync<TEntity>(TEntity entity) where TEntity : IViewModel
        {
            if (entity?.Id == null)
                throw new ArgumentNullException(nameof(entity));

            _cache.TryRemove(entity.Id, out _);

            return Task.CompletedTask;
        }

        public Task<TEntity> GetByIdAsync<TEntity>(int id) where TEntity : class, IViewModel
        {
            if (id == 0)
                throw new ArgumentNullException(nameof(id));

            var success = _cache.TryGetValue(id, out var entity);

            return Task.FromResult<TEntity>(success ? (TEntity)entity : null);
        }

        public Task PersistAsync<TEntity>(TEntity entity) where TEntity : IViewModel
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