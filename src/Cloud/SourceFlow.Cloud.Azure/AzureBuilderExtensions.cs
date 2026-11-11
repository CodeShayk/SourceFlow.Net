using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Hosting;
using SourceFlow.Runtime;

namespace SourceFlow.Cloud.Azure;

/// <summary>Registers the Azure Service Bus transport on the SourceFlow builder.</summary>
public static partial class AzureBuilderExtensions
{
    /// <summary>
    /// Uses Service Bus as the transport (doc 07 §4). Options are validated now, so a value the client cannot honour fails
    /// at startup (GAP-03). Use <see cref="AzureTransportOptions.FullyQualifiedNamespace"/> with a managed identity in
    /// production; a connection string outside the Development environment is logged as a warning.
    /// </summary>
    public static SourceFlowBuilder UseAzureTransport(this SourceFlowBuilder builder, Action<AzureTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new AzureTransportOptions();
        configure(options);
        options.Validate();
        builder.Services.TryAddSingleton(sp =>
        {
            if (options.ConnectionString is { Length: > 0 } && sp.GetService<IHostEnvironment>() is { } environment && !environment.IsDevelopment())
            {
                LogConnectionString(sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AzureTransport)), environment.EnvironmentName);
            }

            return new AzureTransport(options);
        });
        return builder.UseTransport(sp => sp.GetRequiredService<AzureTransport>());
    }

    /// <summary>Adds a readiness check that the service's Service Bus queue is reachable (tags: <c>ready</c>).</summary>
    public static IHealthChecksBuilder AddSourceFlowAzure(this IHealthChecksBuilder builder, string name = "sourceflow-azure")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(name,
            sp => new AzureTransportHealthCheck(sp.GetRequiredService<AzureTransport>(), sp.GetRequiredService<SourceFlowRuntime>().Routing.Endpoint),
            HealthStatus.Unhealthy, ["ready"]));
    }

    [LoggerMessage(EventId = 3401, Level = LogLevel.Warning, Message = "The Service Bus transport uses a connection string in the '{Environment}' environment; use FullyQualifiedNamespace with a managed identity outside Development (doc 07 §6).")]
    private static partial void LogConnectionString(ILogger logger, string environment);
}

/// <summary>Reports whether the service's endpoint queue exists and Service Bus answers.</summary>
public sealed class AzureTransportHealthCheck : IHealthCheck
{
    private readonly AzureTransport _transport;
    private readonly string _endpoint;

    /// <summary>Creates the check.</summary>
    public AzureTransportHealthCheck(AzureTransport transport, string endpoint)
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
                ? HealthCheckResult.Healthy($"The Service Bus queue of '{_endpoint}' is reachable.")
                : HealthCheckResult.Unhealthy($"The Service Bus queue of '{_endpoint}' does not exist.");
        }
        catch (Transport.TransportException e)
        {
            return HealthCheckResult.Unhealthy($"Service Bus is not reachable for '{_endpoint}': {e.Message}", e);
        }
    }
}
