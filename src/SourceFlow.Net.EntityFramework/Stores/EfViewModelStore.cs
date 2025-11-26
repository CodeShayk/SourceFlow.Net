using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SourceFlow.Projections;
using SourceFlow.Stores.EntityFramework.Services;

namespace SourceFlow.Stores.EntityFramework.Stores
{
    public class EfViewModelStore : IViewModelStore
    {
        private readonly ViewModelDbContext _context;
        private readonly IDatabaseResiliencePolicy _resiliencePolicy;
        private readonly IDatabaseTelemetryService _telemetryService;

        public EfViewModelStore(
            ViewModelDbContext context,
            IDatabaseResiliencePolicy resiliencePolicy,
            IDatabaseTelemetryService telemetryService)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _resiliencePolicy = resiliencePolicy ?? throw new ArgumentNullException(nameof(resiliencePolicy));
            _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        }

        public async Task<TViewModel> Get<TViewModel>(int id) where TViewModel : class, IViewModel
        {
            if (id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(id));

            return await _resiliencePolicy.ExecuteAsync(async () =>
            {
                var viewModel = await _context.Set<TViewModel>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == id);

                if (viewModel == null)
                    throw new InvalidOperationException($"ViewModel of type {typeof(TViewModel).Name} with Id {id} not found.");

                return viewModel;
            });
        }

        public async Task Persist<TViewModel>(TViewModel model) where TViewModel : class, IViewModel
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            if (model.Id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(model));

            await _telemetryService.TraceAsync(
                "sourceflow.ef.viewmodel.persist",
                async () =>
                {
                    await _resiliencePolicy.ExecuteAsync(async () =>
                    {
                        // Check if view model exists using AsNoTracking to avoid tracking conflicts
                        var exists = await _context.Set<TViewModel>()
                            .AsNoTracking()
                            .AnyAsync(v => v.Id == model.Id);

                        if (exists)
                            _context.Set<TViewModel>().Update(model);
                        else
                            _context.Set<TViewModel>().Add(model);

                        await _context.SaveChangesAsync();

                        // Detach the view model to avoid tracking conflicts in subsequent operations
                        _context.Entry(model).State = EntityState.Detached;
                    });

                    _telemetryService.RecordViewModelPersisted();
                },
                activity =>
                {
                    activity?.SetTag("sourceflow.viewmodel_id", model.Id);
                    activity?.SetTag("sourceflow.viewmodel_type", typeof(TViewModel).Name);
                });
        }

        public async Task Delete<TViewModel>(TViewModel model) where TViewModel : class, IViewModel
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            if (model.Id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(model));

            await _resiliencePolicy.ExecuteAsync(async () =>
            {
                var viewModelRecord = await _context.Set<TViewModel>()
                    .FirstOrDefaultAsync(v => v.Id == model.Id);

                if (viewModelRecord == null)
                    throw new InvalidOperationException(
                        $"ViewModel of type {typeof(TViewModel).Name} with Id {model.Id} not found.");

                _context.Set<TViewModel>().Remove(viewModelRecord);
                await _context.SaveChangesAsync();
            });
        }
    }
}