using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for a entityStore that provides methods for managing domain entities.
    /// </summary>
    public interface IEntityStoreAdapter
    {
        /// <summary>
        /// Retrieves an entity by unique identifier.
        /// </summary>
        /// <param name="id">Unique Identifier.</param>
        /// <returns></returns>
        Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity;

        /// <summary>
        /// Creates or updates an entity to the entityStore, persisting its state.
        /// </summary>
        /// <param name="entity">Entity Instance.</param>
        /// <returns>The persisted entity</returns>
        Task<TEntity> Persist<TEntity>(TEntity entity) where TEntity : class, IEntity;

        /// <summary>
        /// Deletes an entity from the entityStore.
        /// </summary>
        /// <param name="entity">Entity Instance.</param>
        /// <returns></returns>
        Task Delete<TEntity>(TEntity entity) where TEntity : class, IEntity;
    }
}
