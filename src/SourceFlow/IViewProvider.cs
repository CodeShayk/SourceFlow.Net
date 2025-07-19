using System.Threading.Tasks;
using SourceFlow.ViewModel;

namespace SourceFlow
{
    public interface IViewProvider
    {
        /// <summary>
        /// Retrieves an view model by unique identifier.
        /// </summary>
        /// <param name="id">Unique Identifier.</param>
        /// <returns></returns>
        Task<TViewModel> Find<TViewModel>(int id) where TViewModel : class, IViewModel;

        /// <summary>
        /// Creates or updates an view model to the repository, persisting its state.
        /// </summary>
        /// <param name="entity">ViewModel Instance.</param>
        /// <returns></returns>
        Task Push<TViewModel>(TViewModel model) where TViewModel : IViewModel;
    }
}