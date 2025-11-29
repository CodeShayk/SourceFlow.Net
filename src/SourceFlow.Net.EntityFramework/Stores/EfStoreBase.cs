using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SourceFlow.Stores.EntityFramework.Services;

namespace SourceFlow.Stores.EntityFramework.Stores
{
    public abstract class EfStoreBase<TContext> where TContext : DbContext
    {
        protected readonly TContext Context;
        protected readonly IDatabaseResiliencePolicy ResiliencePolicy;
        protected readonly IDatabaseTelemetryService TelemetryService;

        protected EfStoreBase(
            TContext context,
            IDatabaseResiliencePolicy resiliencePolicy,
            IDatabaseTelemetryService telemetryService)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            ResiliencePolicy = resiliencePolicy ?? throw new ArgumentNullException(nameof(resiliencePolicy));
            TelemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        }

        protected async Task<T> PersistCore<T>(
            T item,
            int id,
            string operationName,
            string itemType,
            Action<Activity, T> setActivityTags,
            Action recordMetric) where T : class
        {
            if (item == null)
                throw new ArgumentNullException(nameof(item));

            if (id <= 0)
                throw new ArgumentException($"{itemType} Id must be greater than 0.", nameof(item));

            await TelemetryService.TraceAsync(
                operationName,
                async () =>
                {
                    await ResiliencePolicy.ExecuteAsync(async () =>
                    {
                        // Check if item exists using AsNoTracking to avoid tracking conflicts
                        var exists = await Context.Set<T>()
                            .AsNoTracking()
                            .AnyAsync(e => EF.Property<int>(e, "Id") == id);

                        if (exists)
                            Context.Set<T>().Update(item);
                        else
                            Context.Set<T>().Add(item);

                        await Context.SaveChangesAsync();

                        // Detach the item to avoid tracking conflicts in subsequent operations
                        Context.Entry(item).State = EntityState.Detached;
                    });

                    recordMetric();
                },
                activity => setActivityTags(activity, item));

            return item;
        }
    }
}
