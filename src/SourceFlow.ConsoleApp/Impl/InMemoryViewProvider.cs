using System.Collections.Concurrent;
using SourceFlow.Projections;

namespace SourceFlow.ConsoleApp.Impl
{
    public class InMemoryViewProvider : IViewProvider
    {
        private readonly ConcurrentDictionary<int, IViewModel> _cache = new();

        public Task Delete<TViewModel>(TViewModel model) where TViewModel : IViewModel
        {
            if (model?.Id == null)
                throw new ArgumentNullException(nameof(model));

            _cache.TryRemove(model.Id, out _);

            return Task.CompletedTask;
        }

        public Task<TViewModel> Find<TViewModel>(int id) where TViewModel : class, IViewModel
        {
            if (id == 0)
                throw new ArgumentNullException(nameof(id));

            var success = _cache.TryGetValue(id, out var model);

            return Task.FromResult<TViewModel>(success ? (TViewModel)model : null);
        }

        public Task Push<TViewModel>(TViewModel model) where TViewModel : IViewModel
        {
            if (model?.Id == null)
                throw new ArgumentNullException(nameof(model));

            if (model.Id == 0)
                model.Id = new Random().Next();

            _cache[model.Id] = model;

            return Task.CompletedTask;
        }
    }
}