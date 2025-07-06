using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IRepository
    {
        /// <summary>
        /// Retrieves an entity by unique identifier.
        /// </summary>
        /// <param name="id">Unique Identifier.</param>
        /// <returns></returns>
        Task<TEntity> GetByIdAsync<TEntity>(int id) where TEntity : class, IEntity;

        /// <summary>
        /// Creates or updates an entity to the repository, persisting its state.
        /// </summary>
        /// <param name="entity">Entity Instance.</param>
        /// <returns></returns>
        Task PersistAsync<TEntity>(TEntity entity) where TEntity : IEntity;

        /// <summary>
        /// Deletes an entity from the repository.
        /// </summary>
        /// <param name="entity">Entity Instance.</param>
        /// <returns></returns>
        Task DeleteAsync<TEntity>(TEntity entity) where TEntity : IEntity;
    }
}