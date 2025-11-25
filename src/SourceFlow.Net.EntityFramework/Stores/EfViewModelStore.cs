using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SourceFlow.Projections;

namespace SourceFlow.Stores.EntityFramework.Stores
{
    public class EfViewModelStore : IViewModelStore
    {
        private readonly ViewModelDbContext _context;

        public EfViewModelStore(ViewModelDbContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<TViewModel> Get<TViewModel>(int id) where TViewModel : class, IViewModel
        {
            if (id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(id));

            var viewModel = await _context.Set<TViewModel>()
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == id);

            if (viewModel == null)
                throw new InvalidOperationException($"ViewModel of type {typeof(TViewModel).Name} with Id {id} not found.");

            return viewModel;
        }

        public async Task Persist<TViewModel>(TViewModel model) where TViewModel : class, IViewModel
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            if (model.Id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(model));

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
        }

        public async Task Delete<TViewModel>(TViewModel model) where TViewModel : class, IViewModel
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            if (model.Id <= 0)
                throw new ArgumentException("ViewModel Id must be greater than 0.", nameof(model));

            var viewModelRecord = await _context.Set<TViewModel>()
                .FirstOrDefaultAsync(v => v.Id == model.Id);

            if (viewModelRecord == null)
                throw new InvalidOperationException(
                    $"ViewModel of type {typeof(TViewModel).Name} with Id {model.Id} not found.");

            _context.Set<TViewModel>().Remove(viewModelRecord);
            await _context.SaveChangesAsync();
        }
    }
}