using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IViewModelFinder
    {
        Task<TViewModel> Find<TViewModel>(int id) where TViewModel : class, IViewModel;
    }
}