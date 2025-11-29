using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SourceFlow.Projections;
using SourceFlow.Stores.EntityFramework.Services;

namespace SourceFlow.Stores.EntityFramework.Stores
{
    public class EfViewModelStore : EfStoreBase<ViewModelDbContext>, IViewModelStore
    {
        public EfViewModelStore(
            ViewModelDbContext context,
            IDatabaseResiliencePolicy resiliencePolicy,
            IDatabaseTelemetryService telemetryService)
            : base(context, resiliencePolicy, telemetryService)
        {
        }

        public async Task<TViewModel> Get<TViewModel>(int id) where TViewModel : class, IViewModel
        {
            if (id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(id));

            return await ResiliencePolicy.ExecuteAsync(async () =>
            {
                var viewModel = await Context.Set<TViewModel>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == id);

                if (viewModel == null)
                    throw new InvalidOperationException($"ViewModel of type {typeof(TViewModel).Name} with Id {id} not found.");

                return viewModel;
            });
        }

        public async Task<TViewModel> Persist<TViewModel>(TViewModel model) where TViewModel : class, IViewModel
        {
            return await PersistCore(
                model,
                model.Id,
                "sourceflow.ef.viewmodel.persist",
                "ViewModel",
                (activity, m) =>
                {
                    activity?.SetTag("sourceflow.viewmodel_id", m.Id);
                    activity?.SetTag("sourceflow.viewmodel_type", typeof(TViewModel).Name);
                },
                () => TelemetryService.RecordViewModelPersisted());
        }

        public async Task Delete<TViewModel>(TViewModel model) where TViewModel : class, IViewModel
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            if (model.Id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(model));

            await ResiliencePolicy.ExecuteAsync(async () =>
            {
                var viewModelRecord = await Context.Set<TViewModel>()
                    .FirstOrDefaultAsync(v => v.Id == model.Id);

                if (viewModelRecord == null)
                    throw new InvalidOperationException(
                        $"ViewModel of type {typeof(TViewModel).Name} with Id {model.Id} not found.");

                Context.Set<TViewModel>().Remove(viewModelRecord);
                await Context.SaveChangesAsync();
            });
        }
    }
}
