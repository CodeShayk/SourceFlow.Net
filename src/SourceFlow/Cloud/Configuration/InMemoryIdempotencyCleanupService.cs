using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace SourceFlow.Cloud.Configuration;

public sealed class InMemoryIdempotencyCleanupService : BackgroundService
{
    private readonly InMemoryIdempotencyService _store;

    public InMemoryIdempotencyCleanupService(InMemoryIdempotencyService store) => _store = store;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _store.RunCleanupAsync(stoppingToken);
}
