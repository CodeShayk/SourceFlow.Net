using Google.Api.Gax.ResourceNames;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SourceFlow.Cloud.GCP.Configuration;

namespace SourceFlow.Cloud.GCP.Infrastructure;

/// <summary>
/// Health check that verifies Pub/Sub connectivity by listing topics in the configured project.
/// </summary>
public class GcpHealthCheck : IHealthCheck
{
    private readonly PublisherServiceApiClient _publisher;
    private readonly GcpOptions _options;

    public GcpHealthCheck(PublisherServiceApiClient publisher, GcpOptions options)
    {
        _publisher = publisher;
        _options = options;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var projectName = ProjectName.FromProject(_options.ProjectId);

            // Enumerate at most one topic to confirm the endpoint is reachable.
            await foreach (var _ in _publisher.ListTopicsAsync(projectName).WithCancellation(cancellationToken))
                break;

            return HealthCheckResult.Healthy("Google Cloud Pub/Sub is accessible");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Google Cloud Pub/Sub is not accessible: {ex.Message}", ex);
        }
    }
}
