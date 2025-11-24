using System.Threading.Tasks;
using SourceFlow.Projections;

namespace SourceFlow.Impl
{
    internal class ViewModelStoreAdapter : IViewModelStoreAdapter
    {
        private readonly IViewModelStore store;

        public ViewModelStoreAdapter(IViewModelStore store) => this.store = store;

        public Task<TViewModel> Find<TViewModel>(int id) where TViewModel : class, IViewModel
        {
            return store.Get<TViewModel>(id);
        }

        public Task Persist<TViewModel>(TViewModel model) where TViewModel : IViewModel
        {
            return store.Persist<TViewModel>(model);
        }

        public Task Delete<TViewModel>(TViewModel entity) where TViewModel : IViewModel
        {
            return store.Delete<TViewModel>(entity);
        }
    }
}