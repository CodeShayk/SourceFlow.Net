using System.Collections.Concurrent;
using System.Reflection;
using SourceFlow.Projections;

namespace SourceFlow.Core.Tests.E2E.Impl
{
    public class InMemoryViewModelStore : IViewModelStore
    {
        private readonly ConcurrentDictionary<int, IViewModel> _cache = new();

        public Task<TViewModel> Get<TViewModel>(int id) where TViewModel : class, IViewModel
        {
            if (id == 0)
                throw new ArgumentNullException(nameof(id));

            var success = _cache.TryGetValue(id, out var model);

            if (!success || model == null)
                throw new InvalidOperationException($"ViewModel not found for ID: {id}");

            return Task.FromResult((TViewModel)model);
        }

        public Task Persist<TViewModel>(TViewModel model) where TViewModel : IViewModel
        {
            if (model?.Id == null)
                throw new ArgumentNullException(nameof(model));

            if (model.Id == 0)
                model.Id = new Random().Next();

            _cache[model.Id] = model;

            return Task.CompletedTask;
        }

        public Task Delete<TViewModel>(TViewModel model) where TViewModel : IViewModel
        {
            if (model?.Id == null)
                throw new ArgumentNullException(nameof(model));

            var success = _cache.Remove(model.Id, out var rmodel);

            if (!success || rmodel == null)
                throw new InvalidOperationException($"ViewModel not found for ID: {model.Id}");


            return Task.CompletedTask;

        }

    }
}