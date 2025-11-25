using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SourceFlow.Stores.EntityFramework;

namespace SourceFlow.Stores.EntityFramework.Stores
{
    public class EfEntityStore : IEntityStore
    {
        private readonly EntityDbContext _context;

        public EfEntityStore(EntityDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity
        {
            if (id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(id));

            var entity = await _context.Set<TEntity>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == id);

            if (entity == null)
                throw new InvalidOperationException($"Entity of type {typeof(TEntity).Name} with Id {id} not found.");


            return entity;
        }

        public async Task Persist<TEntity>(TEntity entity) where TEntity : class, IEntity
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (entity.Id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(entity));

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
        }

        public async Task Delete<TEntity>(TEntity entity) where TEntity : class, IEntity
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (entity.Id <= 0)
                throw new ArgumentException("Entity Id must be greater than 0.", nameof(entity));

            var entityRecord = await _context.Set<TEntity>()
                .FirstOrDefaultAsync(e => e.Id == entity.Id);

            if (entityRecord == null)
                throw new InvalidOperationException(
                    $"Entity of type {typeof(TEntity).Name} with Id {entity.Id} not found.");

            _context.Set<TEntity>().Remove(entityRecord);

            await _context.SaveChangesAsync();
        }
    }
}