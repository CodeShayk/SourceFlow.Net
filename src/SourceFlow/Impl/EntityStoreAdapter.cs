using System.Threading.Tasks;
using SourceFlow.Observability;

namespace SourceFlow.Impl
{
    internal class EntityStoreAdapter : IEntityStoreAdapter
    {
        private readonly IEntityStore store;
        private readonly IDomainTelemetryService telemetry;

        public EntityStoreAdapter(IEntityStore store, IDomainTelemetryService telemetry = null)
        {
            this.store = store;
            this.telemetry = telemetry;
        }

        public Task Delete<TEntity>(TEntity entity) where TEntity : class, IEntity
        {
            if (telemetry != null)
            {
                return telemetry.TraceAsync(
                    "sourceflow.entitystore.delete",
                    () => store.Delete<TEntity>(entity),
                    activity =>
                    {
                        activity?.SetTag("sourceflow.entity_type", typeof(TEntity).Name);
                        activity?.SetTag("sourceflow.entity_id", entity.Id);
                    });
            }

            return store.Delete<TEntity>(entity);
        }

        public Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity
        {
            if (telemetry != null)
            {
                return telemetry.TraceAsync(
                    "sourceflow.entitystore.get",
                    () => store.Get<TEntity>(id),
                    activity =>
                    {
                        activity?.SetTag("sourceflow.entity_type", typeof(TEntity).Name);
                        activity?.SetTag("sourceflow.entity_id", id);
                    });
            }

            return store.Get<TEntity>(id);
        }

        public Task Persist<TEntity>(TEntity entity) where TEntity : class, IEntity
        {
            if (telemetry != null)
            {
                return telemetry.TraceAsync(
                    "sourceflow.entitystore.persist",
                    () => store?.Persist<TEntity>(entity),
                    activity =>
                    {
                        activity?.SetTag("sourceflow.entity_type", typeof(TEntity).Name);
                        activity?.SetTag("sourceflow.entity_id", entity.Id);
                        telemetry.RecordEntityCreated(typeof(TEntity).Name);
                    });
            }

            return store?.Persist<TEntity>(entity);
        }
    }
}