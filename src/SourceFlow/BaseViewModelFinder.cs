using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    /// <summary>
    /// Base class for implementing finders to retrieve view models by fetch criteria.
    /// </summary>
    public abstract class BaseViewModelFinder : IViewModelFinder
    {
        /// <summary>
        /// Repository for managing view models.
        /// </summary>
        protected readonly IViewModelRepository repository;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseViewModelFinder"/> class.
        /// </summary>
        /// <param name="repository"></param>
        /// <exception cref="ArgumentNullException"></exception>
        protected BaseViewModelFinder(IViewModelRepository repository)
        {
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>
        /// Retrieves an view model by unique identifier.
        /// </summary>
        /// <typeparam name="TViewModel"></typeparam>
        /// <param name="id"></param>
        /// <returns></returns>
        public Task<TViewModel> Find<TViewModel>(int id) where TViewModel : class, IViewModel
        {
            return repository.Get<TViewModel>(id);
        }
    }
}