using System.Threading.Tasks;

namespace SourceFlow.Impl
{
    internal class EntityStoreAdapter : IEntityStoreAdapter
    {
        private readonly IEntityStore store;

        public EntityStoreAdapter(IEntityStore store) => this.store = store;

        public Task Delete<TEntity>(TEntity entity) where TEntity : class, IEntity
        {
            return store.Delete<TEntity>(entity);
        }

        public Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity
        {
            return store.Get<TEntity>(id);
        }

        public Task Persist<TEntity>(TEntity entity) where TEntity : class, IEntity
        {
            return store?.Persist<TEntity>(entity);
        }
    }
}