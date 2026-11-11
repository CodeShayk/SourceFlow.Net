using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SourceFlow.Hosting;
using SourceFlow.Runtime;

namespace SourceFlow.Cloud.Gcp;

/// <summary>Registers the Google Cloud Pub/Sub transport on the SourceFlow builder.</summary>
public static class GcpBuilderExtensions
{
    /// <summary>
    /// Uses Pub/Sub as the transport (doc 07 §4). Options are validated now, so a value the client cannot honour fails at
    /// startup (GAP-03). Credentials are Application Default Credentials unless <see cref="GcpTransportOptions.Credential"/>
    /// is set; <c>PUBSUB_EMULATOR_HOST</c> selects the emulator.
    /// </summary>
    public static SourceFlowBuilder UseGcpTransport(this SourceFlowBuilder builder, Action<GcpTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new GcpTransportOptions();
        configure(options);
        options.Validate();
        builder.Services.TryAddSingleton(_ => new GcpTransport(options));
        return builder.UseTransport(sp => sp.GetRequiredService<GcpTransport>());
    }

    /// <summary>Adds a readiness check that the service's Pub/Sub subscription is reachable (tags: <c>ready</c>).</summary>
    public static IHealthChecksBuilder AddSourceFlowGcp(this IHealthChecksBuilder builder, string name = "sourceflow-gcp")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(name,
            sp => new GcpTransportHealthCheck(sp.GetRequiredService<GcpTransport>(), sp.GetRequiredService<SourceFlowRuntime>().Routing.Endpoint),
            HealthStatus.Unhealthy, ["ready"]));
    }
}

/// <summary>Reports whether the service's endpoint subscription exists and Pub/Sub answers.</summary>
public sealed class GcpTransportHealthCheck : IHealthCheck
{
    private readonly GcpTransport _transport;
    private readonly string _endpoint;

    /// <summary>Creates the check.</summary>
    public GcpTransportHealthCheck(GcpTransport transport, string endpoint)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        _endpoint = endpoint;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _transport.IsReachableAsync(_endpoint, cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy($"The Pub/Sub subscription of '{_endpoint}' is reachable.")
                : HealthCheckResult.Unhealthy($"The Pub/Sub subscription of '{_endpoint}' does not exist.");
        }
        catch (Transport.TransportException e)
        {
            return HealthCheckResult.Unhealthy($"Pub/Sub is not reachable for '{_endpoint}': {e.Message}", e);
        }
    }
}
