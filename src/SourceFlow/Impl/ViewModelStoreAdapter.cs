using System.Threading.Tasks;
using SourceFlow.Observability;
using SourceFlow.Projections;

namespace SourceFlow.Impl
{
    internal class ViewModelStoreAdapter : IViewModelStoreAdapter
    {
        private readonly IViewModelStore store;
        private readonly IDomainTelemetryService telemetry;

        public ViewModelStoreAdapter(IViewModelStore store, IDomainTelemetryService telemetry = null)
        {
            this.store = store;
            this.telemetry = telemetry;
        }

        public Task<TViewModel> Find<TViewModel>(int id) where TViewModel : class, IViewModel
        {
            if (telemetry != null)
            {
                return telemetry.TraceAsync(
                    "sourceflow.viewmodelstore.find",
                    () => store.Get<TViewModel>(id),
                    activity =>
                    {
                        activity?.SetTag("sourceflow.viewmodel_type", typeof(TViewModel).Name);
                        activity?.SetTag("sourceflow.viewmodel_id", id);
                    });
            }

            return store.Get<TViewModel>(id);
        }

        public Task<TViewModel> Persist<TViewModel>(TViewModel model) where TViewModel : class, IViewModel
        {
            if (telemetry != null)
            {
                return telemetry.TraceAsync(
                    "sourceflow.viewmodelstore.persist",
                    () => store.Persist<TViewModel>(model),
                    activity =>
                    {
                        activity?.SetTag("sourceflow.viewmodel_type", typeof(TViewModel).Name);
                        activity?.SetTag("sourceflow.viewmodel_id", model.Id);
                    });
            }

            return store.Persist<TViewModel>(model);
        }

        public Task Delete<TViewModel>(TViewModel entity) where TViewModel : class, IViewModel
        {
            if (telemetry != null)
            {
                return telemetry.TraceAsync(
                    "sourceflow.viewmodelstore.delete",
                    () => store.Delete<TViewModel>(entity),
                    activity =>
                    {
                        activity?.SetTag("sourceflow.viewmodel_type", typeof(TViewModel).Name);
                        activity?.SetTag("sourceflow.viewmodel_id", entity.Id);
                    });
            }

            return store.Delete<TViewModel>(entity);
        }
    }
}