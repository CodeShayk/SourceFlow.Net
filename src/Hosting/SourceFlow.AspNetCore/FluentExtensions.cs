using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using SourceFlow.Hosting;

namespace SourceFlow.AspNetCore;

/// <summary>The ASP.NET Core features in the <see cref="SourceFlowBuilder"/> chain, and their endpoints.</summary>
public static class FluentExtensions
{
    /// <summary>
    /// Enables the per-service query API (doc 14 §9): <see cref="QueryApiEndpointRouteBuilderExtensions.AddSourceFlowQueryApi"/> from the builder chain.
    /// Map it with <c>MapSourceFlowQueryApi()</c> and <c>MapDomainQueries()</c>, each with an authorization policy.
    /// </summary>
    public static SourceFlowBuilder WithQueryApi(this SourceFlowBuilder builder, Action<SourceFlowQueryApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSourceFlowQueryApi(configure);
        return builder;
    }

    /// <summary>
    /// Maps the SourceFlow health endpoints (doc 08 §5): <c>{prefix}</c> for liveness (no dependency checks, so an outage of
    /// a database or broker does not restart the service), <c>{prefix}/ready</c> for readiness (the checks tagged
    /// <c>ready</c>: the runtime, the store and the transport) and <c>{prefix}/sourceflow</c> for every SourceFlow check.
    /// Register the checks with <see cref="SourceFlowBuilder.WithHealthChecks"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapSourceFlowHealthChecks(this IEndpointRouteBuilder endpoints, string prefix = "/health")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        string root = prefix.TrimEnd('/');
        endpoints.MapHealthChecks(root, new HealthCheckOptions { Predicate = _ => false });
        endpoints.MapHealthChecks(root + "/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(SourceFlowHealthChecks.ReadyTag) });
        endpoints.MapHealthChecks(root + "/sourceflow", new HealthCheckOptions { Predicate = c => c.Tags.Contains(SourceFlowHealthChecks.Tag) });
        return endpoints;
    }
}
