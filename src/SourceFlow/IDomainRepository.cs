using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Interface for a repository that provides methods for managing domain entities.
    /// </summary>
    public interface IDomainRepository
    {
        /// <summary>
        /// Retrieves an entity by unique identifier.
        /// </summary>
        /// <param name="id">Unique Identifier.</param>
        /// <returns></returns>
        Task<TEntity> Get<TEntity>(int id) where TEntity : class, IEntity;

        /// <summary>
        /// Creates or updates an entity to the repository, persisting its state.
        /// </summary>
        /// <param name="entity">Entity Instance.</param>
        /// <returns></returns>
        Task Persist<TEntity>(TEntity entity) where TEntity : IEntity;

        /// <summary>
        /// Deletes an entity from the repository.
        /// </summary>
        /// <param name="entity">Entity Instance.</param>
        /// <returns></returns>
        Task Delete<TEntity>(TEntity entity) where TEntity : IEntity;
    }
}