using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SourceFlow.Stores.EntityFramework;
using SourceFlow.Stores.EntityFramework.Services;

namespace SourceFlow.Stores.EntityFramework.Stores
{
    public class EfEntityStore : IEntityStore
    {
        private readonly EntityDbContext _context;
        private readonly IDatabaseResiliencePolicy _resiliencePolicy;
        private readonly IDatabaseTelemetryService _telemetryService;

        public EfEntityStore(
            EntityDbContext context,
            IDatabaseResiliencePolicy resiliencePolicy,
            IDatabaseTelemetryService telemetryService)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _resiliencePolicy = resiliencePolicy ?? throw new ArgumentNullException(nameof(resiliencePolicy));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        }

        public async Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity
        {
            if (id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(id));

            return await _resiliencePolicy.ExecuteAsync(async () =>
            {
                var entity = await _context.Set<TEntity>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == id);

                if (entity == null)
                    throw new InvalidOperationException($"Entity of type {typeof(TEntity).Name} with Id {id} not found.");

                return entity;
            });
        }

        public async Task Persist<TEntity>(TEntity entity) where TEntity : class, IEntity
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (entity.Id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(entity));

            await _telemetryService.TraceAsync(
                "sourceflow.ef.entity.persist",
                async () =>
                {
                    await _resiliencePolicy.ExecuteAsync(async () =>
                    {
                        // Check if entity exists using AsNoTracking to avoid tracking conflicts
                        var exists = await _context.Set<TEntity>()
                            .AsNoTracking()
                            .AnyAsync(e => e.Id == entity.Id);

                        if (exists)
                            _context.Set<TEntity>().Update(entity);
                        else
                            _context.Set<TEntity>().Add(entity);

                        await _context.SaveChangesAsync();

                        // Detach the entity to avoid tracking conflicts in subsequent operations
                        _context.Entry(entity).State = EntityState.Detached;
                    });

                    _telemetryService.RecordEntityPersisted();
                },
                activity =>
                {
                    activity?.SetTag("sourceflow.entity_id", entity.Id);
                    activity?.SetTag("sourceflow.entity_type", typeof(TEntity).Name);
                });
        }

        public async Task Delete<TEntity>(TEntity entity) where TEntity : class, IEntity
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (entity.Id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(entity));

            await _resiliencePolicy.ExecuteAsync(async () =>
            {
                var entityRecord = await _context.Set<TEntity>()
                    .FirstOrDefaultAsync(e => e.Id == entity.Id);

                if (entityRecord == null)
                    throw new InvalidOperationException(
                        $"Entity of type {typeof(TEntity).Name} with Id {entity.Id} not found.");

                _context.Set<TEntity>().Remove(entityRecord);

                await _context.SaveChangesAsync();
            });
        }
    }
}