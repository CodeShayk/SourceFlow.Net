using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public abstract class BaseViewModelFinder : IViewModelFinder
    {
        protected IViewRepository viewRepository;

        public async Task<TViewModel> FindProjection<TViewModel>(int id) where TViewModel : class, IViewModel
        {
            var viewModel = await viewRepository.GetByIdAsync<TViewModel>(id);

            if (viewModel == null)
                throw new InvalidOperationException($"No projection found for ID {id}");

            return viewModel;
        }
    }
}