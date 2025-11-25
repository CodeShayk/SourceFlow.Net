using System.Threading.Tasks;
using SourceFlow.Projections;

namespace SourceFlow
{
    public interface IViewModelStoreAdapter
    {
        /// <summary>
        /// Retrieves an view model by unique identifier.
        /// </summary>
        /// <param name="id">Unique Identifier.</param>
        /// <returns></returns>
        Task<TViewModel> Find<TViewModel>(int id) where TViewModel : class, IViewModel;

        /// <summary>
        /// Creates or updates an view model to the entityStore, persisting its state.
        /// </summary>
        /// <param name="entity">ViewModel Instance.</param>
        /// <returns></returns>
        Task Persist<TViewModel>(TViewModel model) where TViewModel : class, IViewModel;


        /// <summary>
        /// Deletes a ViewModel, could implement soft or hard delete.
        /// </summary>
        /// <typeparam name="TViewModel"></typeparam>
        /// <param name="model"></param>
        /// <returns></returns>
        Task Delete<TViewModel>(TViewModel model) where TViewModel : class, IViewModel;
    }
}