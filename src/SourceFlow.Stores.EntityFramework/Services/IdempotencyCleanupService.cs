using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SourceFlow.Stores.EntityFramework.Services;

/// <summary>
/// Background service that periodically cleans up expired idempotency records
/// </summary>
public class IdempotencyCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _cleanupInterval;
    private readonly ILogger<IdempotencyCleanupService> _logger;

    public IdempotencyCleanupService(
        IServiceProvider serviceProvider,
        TimeSpan cleanupInterval)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _cleanupInterval = cleanupInterval;
        
        // Try to get logger, but don't fail if not available
        _logger = serviceProvider.GetService<ILogger<IdempotencyCleanupService>>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IdempotencyCleanupService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Idempotency cleanup service started. Cleanup interval: {Interval} minutes",
            _cleanupInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                    break;

                await CleanupExpiredRecordsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during idempotency cleanup cycle");
                // Continue running despite errors
            }
        }

        _logger.LogInformation("Idempotency cleanup service stopped");
    }

    private async Task CleanupExpiredRecordsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var idempotencyService = scope.ServiceProvider.GetRequiredService<EfIdempotencyService>();

            await idempotencyService.CleanupExpiredRecordsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup expired idempotency records");
        }
    }
}
