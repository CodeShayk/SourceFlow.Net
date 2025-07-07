using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public interface IViewModelFinder
    {
        Task<TViewModel> FindProjection<TViewModel>(int id) where TViewModel : class, IViewModel;
    }
}