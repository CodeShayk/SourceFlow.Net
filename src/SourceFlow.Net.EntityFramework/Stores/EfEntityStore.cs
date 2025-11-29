using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SourceFlow.Stores.EntityFramework.Services;

namespace SourceFlow.Stores.EntityFramework.Stores
{
    public class EfEntityStore : EfStoreBase<EntityDbContext>, IEntityStore
    {
        public EfEntityStore(
            EntityDbContext context,
            IDatabaseResiliencePolicy resiliencePolicy,
            IDatabaseTelemetryService telemetryService)
            : base(context, resiliencePolicy, telemetryService)
        {
        }

        public async Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity
        {
            if (id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(id));

            return await ResiliencePolicy.ExecuteAsync(async () =>
            {
                var entity = await Context.Set<TEntity>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == id);

                if (entity == null)
                    throw new InvalidOperationException($"Entity of type {typeof(TEntity).Name} with Id {id} not found.");

                return entity;
            });
        }

        public async Task<TEntity> Persist<TEntity>(TEntity entity) where TEntity : class, IEntity
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            return await PersistCore(
                entity,
                entity.Id,
                "sourceflow.ef.entity.persist",
                "Entity",
                (activity, e) =>
                {
                    activity?.SetTag("sourceflow.entity_id", e.Id);
                    activity?.SetTag("sourceflow.entity_type", typeof(TEntity).Name);
                },
                () => TelemetryService.RecordEntityPersisted());
        }

        public async Task Delete<TEntity>(TEntity entity) where TEntity : class, IEntity
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (entity.Id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(entity));

            await ResiliencePolicy.ExecuteAsync(async () =>
            {
                var entityRecord = await Context.Set<TEntity>()
                    .FirstOrDefaultAsync(e => e.Id == entity.Id);

                if (entityRecord == null)
                    throw new InvalidOperationException(
                        $"Entity of type {typeof(TEntity).Name} with Id {entity.Id} not found.");

                Context.Set<TEntity>().Remove(entityRecord);

                await Context.SaveChangesAsync();
            });
        }
    }
}
