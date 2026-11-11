using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SourceFlow.Hosting;
using SourceFlow.Runtime;

namespace SourceFlow.Cloud.Aws;

/// <summary>Registers the AWS transport on the SourceFlow builder.</summary>
public static class AwsBuilderExtensions
{
    /// <summary>
    /// Uses SQS and SNS as the transport (doc 07 §4). Options are validated now, so a value the SDK cannot honour fails
    /// at startup (GAP-03). Credentials come from the SDK credential chain, or <see cref="AwsTransportOptions.Profile"/>.
    /// </summary>
    public static SourceFlowBuilder UseAwsTransport(this SourceFlowBuilder builder, Action<AwsTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new AwsTransportOptions();
        configure(options);
        options.Validate();
        builder.Services.TryAddSingleton(_ => new AwsTransport(options));
        return builder.UseTransport(sp => sp.GetRequiredService<AwsTransport>());
    }

    /// <summary>Adds a readiness check that the service's SQS queue is reachable (tags: <c>ready</c>).</summary>
    public static IHealthChecksBuilder AddSourceFlowAws(this IHealthChecksBuilder builder, string name = "sourceflow-aws")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(name,
            sp => new AwsTransportHealthCheck(sp.GetRequiredService<AwsTransport>(), sp.GetRequiredService<SourceFlowRuntime>().Routing.Endpoint),
            HealthStatus.Unhealthy, ["ready"]));
    }
}

/// <summary>Reports whether the service's endpoint queue exists and SQS answers.</summary>
public sealed class AwsTransportHealthCheck : IHealthCheck
{
    private readonly AwsTransport _transport;
    private readonly string _endpoint;

    /// <summary>Creates the check.</summary>
    public AwsTransportHealthCheck(AwsTransport transport, string endpoint)
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
                ? HealthCheckResult.Healthy($"The SQS queue of '{_endpoint}' is reachable.")
                : HealthCheckResult.Unhealthy($"The SQS queue of '{_endpoint}' does not exist.");
        }
        catch (Transport.TransportException e)
        {
            return HealthCheckResult.Unhealthy($"SQS is not reachable for '{_endpoint}': {e.Message}", e);
        }
    }
}
