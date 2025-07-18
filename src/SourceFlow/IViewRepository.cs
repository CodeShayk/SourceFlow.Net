using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IViewRepository
    {
        /// <summary>
        /// Retrieves an view model by unique identifier.
        /// </summary>
        /// <param name="id">Unique Identifier.</param>
        /// <returns></returns>
        Task<TViewModel> Get<TViewModel>(int id) where TViewModel : class, IViewModel;

        /// <summary>
        /// Creates or updates an view model to the repository, persisting its state.
        /// </summary>
        /// <param name="entity">ViewModel Instance.</param>
        /// <returns></returns>
        Task Persist<TViewModel>(TViewModel model) where TViewModel : IViewModel;

        /// <summary>
        /// Deletes an view model from the repository.
        /// </summary>
        /// <param name="model">ViewModel Instance.</param>
        /// <returns></returns>
        Task Delete<TViewModel>(TViewModel model) where TViewModel : IViewModel;
    }
}