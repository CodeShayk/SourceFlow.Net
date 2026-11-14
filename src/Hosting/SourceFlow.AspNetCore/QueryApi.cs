using System.Buffers;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Query;
using SourceFlow.Runtime;

namespace SourceFlow.AspNetCore;

/// <summary>Options of the per-service query API (doc 14 §9).</summary>
public sealed class SourceFlowQueryApiOptions
{
    /// <summary>The largest request body (default 64 KB; larger → 413).</summary>
    public int MaxBodyBytes { get; set; } = 64 * 1024;

    /// <summary>The deepest JSON nesting in a request body (default 16).</summary>
    public int MaxJsonDepth { get; set; } = 16;

    /// <summary>Requests per minute per principal (default 60; over → 429 SFQ0502).</summary>
    public int RequestsPerMinute { get; set; } = 60;

    /// <summary>Concurrent queries per principal (default 4; over → 429 SFQ0503).</summary>
    public int ConcurrencyPerPrincipal { get; set; } = 4;

    /// <summary>Concurrent queries for the service (default 32; over → 503 SFQ0503).</summary>
    public int ConcurrencyPerService { get; set; } = 32;

    /// <summary>
    /// The caller's query permissions. The default reads the <c>sourceflow.query.*</c> values of the <c>permission</c>,
    /// <c>permissions</c>, <c>scope</c> and <c>scp</c> claims (space-separated scopes are split).
    /// </summary>
    public Func<ClaimsPrincipal, QueryPermissions> Permissions { get; set; } = DefaultPermissions;

    /// <summary>The caller's tenant. The default reads the <c>sourceflow:tenant</c> or <c>tenant</c> claim; null uses the ambient tenant.</summary>
    public Func<HttpContext, string?> Tenant { get; set; } = context =>
        context.User.FindFirst("sourceflow:tenant")?.Value ?? context.User.FindFirst("tenant")?.Value;

    /// <summary>The default permission mapping (see <see cref="Permissions"/>).</summary>
    public static QueryPermissions DefaultPermissions(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        string[] granted =
        [
            .. principal.Claims
                .Where(c => c.Type is "permission" or "permissions" or "scope" or "scp")
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(v => v.StartsWith("sourceflow.query.", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];
        return QueryPermissions.Of(granted);
    }
}

/// <summary>Marks endpoints of the query API, so startup can check they carry an authorization decision (SFQ0504).</summary>
public sealed class SourceFlowQueryApiEndpointMetadata;

/// <summary>Maps the per-service query API and named domain queries (doc 14 §9.1, Q-B4, ADR-026).</summary>
public static class QueryApiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Registers the query API's options, limiters and the fail-closed startup check: every endpoint mapped by
    /// <see cref="MapSourceFlowQueryApi"/> or <see cref="MapDomainQueries"/> must have <c>RequireAuthorization(…)</c> (or an
    /// explicit <c>AllowAnonymous()</c>), or startup fails (SFQ0504).
    /// </summary>
    public static IServiceCollection AddSourceFlowQueryApi(this IServiceCollection services, Action<SourceFlowQueryApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new SourceFlowQueryApiOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton(new QueryApiLimiter(options));
        services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, QueryApiAuthorizationCheck>();
        return services;
    }

    /// <summary>
    /// Maps <c>POST {prefix}</c>, <c>POST {prefix}/validate</c>, <c>POST {prefix}/explain</c> and <c>GET {prefix}/schema</c>.
    /// Attach an authorization policy to the returned group: <c>.RequireAuthorization("sourceflow-query")</c>.
    /// </summary>
    public static RouteGroupBuilder MapSourceFlowQueryApi(this IEndpointRouteBuilder endpoints, string prefix = "/query")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Require(endpoints);
        RouteGroupBuilder group = endpoints.MapGroup(prefix).WithMetadata(new SourceFlowQueryApiEndpointMetadata()).WithTags("SourceFlow query");
        group.MapPost(string.Empty, (RequestDelegate)QueryApi.QueryAsync).WithName("SourceFlowQuery").WithSummary("Run an ad-hoc SFQL or SQL-syntax query.");
        group.MapPost("/validate", (RequestDelegate)QueryApi.ValidateAsync).WithName("SourceFlowQueryValidate").WithSummary("Parse, bind and policy-check a query without running it.");
        group.MapPost("/explain", (RequestDelegate)QueryApi.ExplainAsync).WithName("SourceFlowQueryExplain").WithSummary("Show the bound plan and the parameterised statement.");
        group.MapGet("/schema", (RequestDelegate)QueryApi.SchemaAsync).WithName("SourceFlowQuerySchema").WithSummary("The service's queryable sources and fields.");
        return group;
    }

    /// <summary>
    /// Maps the named domain queries (Q-B4): <c>GET {prefix}</c> lists them and <c>GET {prefix}/{name}/v{version}</c> runs one
    /// with its parameters from the query string. Attach an authorization policy to the returned group.
    /// </summary>
    public static RouteGroupBuilder MapDomainQueries(this IEndpointRouteBuilder endpoints, string prefix = "/queries")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Require(endpoints);
        RouteGroupBuilder group = endpoints.MapGroup(prefix).WithMetadata(new SourceFlowQueryApiEndpointMetadata()).WithTags("SourceFlow domain queries");
        group.MapGet(string.Empty, (RequestDelegate)QueryApi.ListDomainQueriesAsync).WithName("SourceFlowDomainQueries").WithSummary("The named domain queries.");
        group.MapGet("/{name}/v{version:int}", (RequestDelegate)QueryApi.DomainQueryAsync).WithName("SourceFlowDomainQuery").WithSummary("Run a named domain query.");
        return group;
    }

    private static void Require(IEndpointRouteBuilder endpoints)
    {
        if (endpoints.ServiceProvider.GetService<SourceFlowQueryApiOptions>() is null)
        {
            throw new InvalidOperationException("Call services.AddSourceFlowQueryApi() before mapping the query API.");
        }
    }
}

/// <summary>
/// Fails startup when a query API endpoint has no authorization decision (doc 14 §9.1, SFQ0504). It runs when the request
/// pipeline is built, after routing has attached the mapped endpoints.
/// </summary>
internal sealed class QueryApiAuthorizationCheck : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            next(app);
            string[] open =
            [
                .. app.ApplicationServices.GetRequiredService<EndpointDataSource>().Endpoints
                    .Where(e => e.Metadata.GetMetadata<SourceFlowQueryApiEndpointMetadata>() is not null)
                    .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
                    .Select(e => e.DisplayName ?? "?"),
            ];
            if (open.Length > 0)
            {
                throw new InvalidOperationException(
                    $"{QueryCodes.AuthorizationRequired}: the SourceFlow query API is mapped without an authorization policy ({string.Join(", ", open)}). " +
                    "Add .RequireAuthorization(\"policy\") to the group (or an explicit .AllowAnonymous() for local demos).");
            }

            CheckDomainQueryPolicies(app.ApplicationServices);
        };
    }

    // A domain query's [DomainQuery(Policy = …)] must name a policy that exists: an unknown one would fail every call.
    // Only the default provider is checked; a custom IAuthorizationPolicyProvider may create policies on demand.
    private static void CheckDomainQueryPolicies(IServiceProvider services)
    {
        bool mapped = services.GetRequiredService<EndpointDataSource>().Endpoints
            .Any(e => e.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName == "SourceFlowDomainQuery");
        string[] policies = mapped && services.GetService<RuntimeModel>() is { } model
            ? [.. model.DomainQueries.Select(q => q.Policy).OfType<string>().Distinct(StringComparer.Ordinal)]
            : [];
        if (policies.Length == 0)
        {
            return;
        }

        if (services.GetService<IAuthorizationService>() is null)
        {
            throw new InvalidOperationException(
                $"{QueryCodes.AuthorizationRequired}: domain queries declare authorization policies ({string.Join(", ", policies)}) but authorization isn't registered: call services.AddAuthorization(…).");
        }

        if (services.GetService<IAuthorizationPolicyProvider>() is DefaultAuthorizationPolicyProvider)
        {
            AuthorizationOptions options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthorizationOptions>>().Value;
            string[] missing = [.. policies.Where(p => options.GetPolicy(p) is null)];
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"{QueryCodes.AuthorizationRequired}: domain queries name authorization policies that aren't registered ({string.Join(", ", missing)}).");
            }
        }
    }
}

/// <summary>Per-principal rate and concurrency limits and a per-service concurrency limit (SFQ0502/0503).</summary>
internal sealed class QueryApiLimiter(SourceFlowQueryApiOptions options)
{
    private readonly PartitionedRateLimiter<string> _rate = PartitionedRateLimiter.Create<string, string>(principal =>
        RateLimitPartition.GetTokenBucketLimiter(principal, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = Math.Max(1, options.RequestsPerMinute),
            TokensPerPeriod = Math.Max(1, options.RequestsPerMinute),
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    private readonly PartitionedRateLimiter<string> _perPrincipal = PartitionedRateLimiter.Create<string, string>(principal =>
        RateLimitPartition.GetConcurrencyLimiter(principal, _ => new ConcurrencyLimiterOptions { PermitLimit = Math.Max(1, options.ConcurrencyPerPrincipal), QueueLimit = 0 }));

    private readonly ConcurrencyLimiter _service = new(new ConcurrencyLimiterOptions { PermitLimit = Math.Max(1, options.ConcurrencyPerService), QueueLimit = 0 });

    /// <summary>Acquires the permits for one request, or returns the rejection (status, code, retry-after seconds).</summary>
    public (IDisposable? Lease, int Status, string? Code, int? RetryAfter) Acquire(string principal)
    {
        using RateLimitLease rate = _rate.AttemptAcquire(principal);
        if (!rate.IsAcquired)
        {
            int? retry = rate.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan after) ? (int)Math.Ceiling(after.TotalSeconds) : 60;
            return (null, StatusCodes.Status429TooManyRequests, QueryCodes.RateLimited, retry);
        }

        RateLimitLease mine = _perPrincipal.AttemptAcquire(principal);
        if (!mine.IsAcquired)
        {
            mine.Dispose();
            return (null, StatusCodes.Status429TooManyRequests, QueryCodes.ConcurrencyLimited, 1);
        }

        RateLimitLease service = _service.AttemptAcquire();
        if (!service.IsAcquired)
        {
            service.Dispose();
            mine.Dispose();
            return (null, StatusCodes.Status503ServiceUnavailable, QueryCodes.ConcurrencyLimited, 1);
        }

        return (new Leases(mine, service), 200, null, null);
    }

    private sealed class Leases(RateLimitLease first, RateLimitLease second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }
}

/// <summary>The query API's request handling (doc 14 §9.2): transport guard, limits, permissions, execution and serialisation.</summary>
internal static class QueryApi
{
    private const string NdJson = "application/x-ndjson";
    private const string Csv = "text/csv";

    public static Task QueryAsync(HttpContext http)
    {
        return HandleAsync(http, QueryPermission.Adhoc, async (request, options, services) =>
        {
            QueryPage page = await services.GetRequiredService<IQueryService>().QueryPageAsync(request.Query, request.Parameters, request.Continuation, options with { Language = request.Language }, http.RequestAborted).ConfigureAwait(false);
            await WritePageAsync(http, page).ConfigureAwait(false);
        });
    }

    public static Task ValidateAsync(HttpContext http)
    {
        return HandleAsync(http, QueryPermission.Validate, async (request, options, services) =>
        {
            QueryValidation validation = await services.GetRequiredService<IQueryService>().ValidateAsync(request.Query, request.Parameters, options with { Language = request.Language }, http.RequestAborted).ConfigureAwait(false);
            await WriteJsonAsync(http, StatusCodes.Status200OK, writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("valid", validation.IsValid);
                WriteDiagnostics(writer, validation.Diagnostics);
                WriteColumns(writer, validation.Columns);
                writer.WriteEndObject();
            }).ConfigureAwait(false);
        });
    }

    public static Task ExplainAsync(HttpContext http)
    {
        return HandleAsync(http, QueryPermission.Explain, async (request, options, services) =>
        {
            QueryPlan plan = await services.GetRequiredService<IQueryService>().ExplainAsync(request.Query, request.Parameters, options with { Language = request.Language }, http.RequestAborted).ConfigureAwait(false);
            await WriteJsonAsync(http, StatusCodes.Status200OK, writer =>
            {
                writer.WriteStartObject();
                WriteColumns(writer, plan.Columns);
                WriteConsistency(writer, plan.Sources);
                writer.WriteString("engine", plan.Engine);
                writer.WriteString("statement", plan.Statement);
                writer.WriteNumber("parameterCount", plan.ParameterCount);
                writer.WriteString("queryHash", plan.QueryHash);
                writer.WriteEndObject();
            }).ConfigureAwait(false);
        });
    }

    public static async Task SchemaAsync(HttpContext http)
    {
        SecurityHeaders(http);
        if (!Permit(http, QueryPermission.Schema, out _))
        {
            await ProblemAsync(http, StatusCodes.Status403Forbidden, "The caller lacks the permission for this endpoint.", QueryCodes.PermissionDenied, []).ConfigureAwait(false);
            return;
        }

        QueryCatalog catalog = QueryCatalog.Build(http.RequestServices.GetRequiredService<RuntimeModel>());
        await WriteJsonAsync(http, StatusCodes.Status200OK, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("fingerprint", catalog.Fingerprint);
            writer.WriteStartArray("events");
            foreach (QueryEventType type in catalog.EventTypes.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", type.Name);
                writer.WriteStartArray("versions");
                foreach (QueryEventVersion version in type.Versions)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", version.Version);
                    WriteFields(writer, version.Fields);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("readModels");
            foreach (QueryReadModel model in catalog.ReadModels.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", model.Name);
                writer.WriteString("collection", model.Collection);
                WriteFields(writer, model.Fields);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("aggregates");
            foreach (QueryAggregate aggregate in catalog.Aggregates.OrderBy(a => a.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", aggregate.Name);
                WriteFields(writer, aggregate.Fields);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    public static async Task ListDomainQueriesAsync(HttpContext http)
    {
        SecurityHeaders(http);
        RuntimeModel model = http.RequestServices.GetRequiredService<RuntimeModel>();
        var visible = new List<DomainQueryDescriptor>();
        foreach (DomainQueryDescriptor query in model.DomainQueries)
        {
            if (await AuthorizedAsync(http, query).ConfigureAwait(false))
            {
                visible.Add(query);
            }
        }

        await WriteJsonAsync(http, StatusCodes.Status200OK, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("queries");
            foreach (DomainQueryDescriptor query in visible)
            {
                writer.WriteStartObject();
                writer.WriteString("name", query.Name);
                writer.WriteNumber("version", query.Version);
                writer.WriteString("path", $"{query.Name}/v{query.Version.ToString(CultureInfo.InvariantCulture)}");
                writer.WriteStartArray("parameters");
                foreach (DomainQueryParameter parameter in query.Parameters)
                {
                    writer.WriteStringValue(parameter.Name);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("columns");
                foreach (string column in query.ResultColumns)
                {
                    writer.WriteStringValue(column);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    public static async Task DomainQueryAsync(HttpContext http)
    {
        SecurityHeaders(http);
        string name = (string)http.Request.RouteValues["name"]!;
        int version = int.Parse((string)http.Request.RouteValues["version"]!, CultureInfo.InvariantCulture);
        DomainQueryDescriptor? query = http.RequestServices.GetRequiredService<RuntimeModel>().DomainQueries.FirstOrDefault(q => q.Name == name && q.Version == version);
        if (query is null)
        {
            await ProblemAsync(http, StatusCodes.Status404NotFound, "The domain query doesn't exist.", null, []).ConfigureAwait(false);
            return;
        }

        if (!await AuthorizedAsync(http, query).ConfigureAwait(false))
        {
            await ProblemAsync(http, StatusCodes.Status403Forbidden, "The caller isn't authorized to run this domain query.", QueryCodes.PermissionDenied, []).ConfigureAwait(false);
            return;
        }

        SourceFlowQueryApiOptions settings = http.RequestServices.GetRequiredService<SourceFlowQueryApiOptions>();
        (IDisposable? lease, int status, string? code, int? retry) = http.RequestServices.GetRequiredService<QueryApiLimiter>().Acquire(PrincipalKey(http.User));
        if (lease is null)
        {
            await ThrottledAsync(http, status, code!, retry).ConfigureAwait(false);
            return;
        }

        using (lease)
        {
            // A named query is a contract (Q-B4): it runs with read permissions; personal data still needs the caller's PII grant.
            QueryPermissions caller = settings.Permissions(http.User);
            var granted = new List<string> { QueryPermission.Events, QueryPermission.ReadModels, QueryPermission.Aggregates, QueryPermission.Schema };
            if (caller.Has(QueryPermission.PersonalData))
            {
                granted.Add(QueryPermission.PersonalData);
            }

            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DomainQueryParameter parameter in query.Parameters)
            {
                parameters[parameter.Name] = http.Request.Query.TryGetValue(parameter.Name, out Microsoft.Extensions.Primitives.StringValues value) ? value.ToString() : null;
            }

            int? pageSize = int.TryParse(http.Request.Query["pageSize"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) ? size : null;
            string? continuation = http.Request.Query["continuation"].ToString() is { Length: > 0 } token ? token : null;
            var options = new QueryOptions { Language = query.Language, Permissions = QueryPermissions.Of([.. granted]), PageSize = pageSize, Tenant = settings.Tenant(http) };
            try
            {
                QueryPage page = await http.RequestServices.GetRequiredService<IQueryService>().QueryPageAsync(query.Text, parameters, continuation, options, http.RequestAborted).ConfigureAwait(false);
                await WritePageAsync(http, page).ConfigureAwait(false);
            }
            catch (QueryException e)
            {
                await ProblemAsync(http, StatusOf(e.Code), "The query was rejected.", e.Code, e.Diagnostics).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await ProblemAsync(http, StatusCodes.Status504GatewayTimeout, "The query timed out.", "QueryTimeout", []).ConfigureAwait(false);
            }
        }
    }

    // ================================================================ request pipeline (doc 14 §9.2)

    private sealed record Request(string Query, QueryLanguage Language, IReadOnlyDictionary<string, object?> Parameters, string? Continuation, int? PageSize);

    private static async Task HandleAsync(HttpContext http, string permission, Func<Request, QueryOptions, IServiceProvider, Task> handle)
    {
        SecurityHeaders(http);
        if (!Permit(http, permission, out QueryOptions? options))
        {
            await ProblemAsync(http, StatusCodes.Status403Forbidden, "The caller lacks the permission for this endpoint.", QueryCodes.PermissionDenied, []).ConfigureAwait(false);
            return;
        }

        (IDisposable? lease, int status, string? code, int? retry) = http.RequestServices.GetRequiredService<QueryApiLimiter>().Acquire(PrincipalKey(http.User));
        if (lease is null)
        {
            await ThrottledAsync(http, status, code!, retry).ConfigureAwait(false);
            return;
        }

        using (lease)
        {
            Request? request = await ReadAsync(http).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            try
            {
                await handle(request, options! with { PageSize = request.PageSize }, http.RequestServices).ConfigureAwait(false);
            }
            catch (QueryException e)
            {
                await ProblemAsync(http, StatusOf(e.Code), "The query is not valid.", e.Code, e.Diagnostics).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await ProblemAsync(http, StatusCodes.Status504GatewayTimeout, "The query timed out.", "QueryTimeout", []).ConfigureAwait(false);
            }
        }
    }

    private static bool Permit(HttpContext http, string permission, out QueryOptions? options)
    {
        SourceFlowQueryApiOptions settings = http.RequestServices.GetRequiredService<SourceFlowQueryApiOptions>();
        QueryPermissions permissions = settings.Permissions(http.User);
        options = new QueryOptions { Permissions = permissions, Tenant = settings.Tenant(http) };
        return permissions.Has(permission);
    }

    // Transport guard: JSON only (415), body ≤ MaxBodyBytes (413), JSON depth ≤ MaxJsonDepth and the request shape (400).
    private static async Task<Request?> ReadAsync(HttpContext http)
    {
        SourceFlowQueryApiOptions settings = http.RequestServices.GetRequiredService<SourceFlowQueryApiOptions>();
        if (http.Request.ContentType is not { } type || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await ProblemAsync(http, StatusCodes.Status415UnsupportedMediaType, "The request body must be application/json.", null, []).ConfigureAwait(false);
            return null;
        }

        if (http.Request.ContentLength > settings.MaxBodyBytes)
        {
            await ProblemAsync(http, StatusCodes.Status413PayloadTooLarge, "The request body is too large.", QueryCodes.Limit, []).ConfigureAwait(false);
            return null;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(settings.MaxBodyBytes + 1);
        try
        {
            int length = 0;
            int read;
            while (length <= settings.MaxBodyBytes && (read = await http.Request.Body.ReadAsync(buffer.AsMemory(length, settings.MaxBodyBytes + 1 - length), http.RequestAborted).ConfigureAwait(false)) > 0)
            {
                length += read;
            }

            if (length > settings.MaxBodyBytes)
            {
                await ProblemAsync(http, StatusCodes.Status413PayloadTooLarge, "The request body is too large.", QueryCodes.Limit, []).ConfigureAwait(false);
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = settings.MaxJsonDepth });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("query", out JsonElement query) || query.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("The body must be an object with a 'query' string.");
            }

            QueryLanguage language = root.TryGetProperty("language", out JsonElement l) && string.Equals(l.GetString(), "sql", StringComparison.OrdinalIgnoreCase)
                ? QueryLanguage.Sql
                : QueryLanguage.Sfql;
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (root.TryGetProperty("parameters", out JsonElement p) && p.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty parameter in p.EnumerateObject())
                {
                    parameters[parameter.Name] = parameter.Value.Clone();
                }
            }

            string? continuation = root.TryGetProperty("continuation", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            int? pageSize = root.TryGetProperty("pageSize", out JsonElement s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out int size) ? size : null;
            return new Request(query.GetString()!, language, parameters, continuation, pageSize);
        }
        catch (JsonException)
        {
            await ProblemAsync(http, StatusCodes.Status400BadRequest, "The request body is not valid JSON for a query request.", null, []).ConfigureAwait(false);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ================================================================ responses

    private static void SecurityHeaders(HttpContext http)
    {
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";   // no cross-origin embedding of results (DAST)
    }

    private static async Task WritePageAsync(HttpContext http, QueryPage page)
    {
        string accept = http.Request.Headers.Accept.ToString();
        if (accept.Contains(NdJson, StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = NdJson;
            foreach (QueryRow row in page.Rows)
            {
                await WriteLineAsync(http, writer => WriteRowObject(writer, row)).ConfigureAwait(false);
            }

            await WriteLineAsync(http, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("continuation", page.Continuation);
                WriteConsistency(writer, page.Consistency);
                writer.WriteString("queryHash", page.QueryHash);
                writer.WriteEndObject();
            }).ConfigureAwait(false);
            return;
        }

        if (accept.Contains(Csv, StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = Csv + "; charset=utf-8";
            var csv = new StringBuilder();
            csv.AppendJoin(',', page.Columns.Select(c => CsvCell(c.Name))).Append("\r\n");
            foreach (QueryRow row in page.Rows)
            {
                csv.AppendJoin(',', row.Values.Select(v => CsvCell(Text(v)))).Append("\r\n");
            }

            if (page.Continuation is { } next)
            {
                http.Response.Headers["SourceFlow-Continuation"] = next;
            }

            await http.Response.WriteAsync(csv.ToString(), http.RequestAborted).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(http, StatusCodes.Status200OK, writer =>
        {
            writer.WriteStartObject();
            WriteColumns(writer, page.Columns);
            writer.WriteStartArray("rows");
            foreach (QueryRow row in page.Rows)
            {
                writer.WriteStartArray();
                foreach (object? value in row.Values)
                {
                    WriteValue(writer, value);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteString("continuation", page.Continuation);
            WriteConsistency(writer, page.Consistency);
            writer.WriteString("queryHash", page.QueryHash);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    private static void WriteRowObject(Utf8JsonWriter writer, QueryRow row)
    {
        writer.WriteStartObject();
        for (int i = 0; i < row.Columns.Count; i++)
        {
            writer.WritePropertyName(row.Columns[i].Name);
            WriteValue(writer, row[i]);
        }

        writer.WriteEndObject();
    }

    // S-4: cells starting with = + - @ TAB or CR are prefixed with ' so spreadsheets don't run them as formulas.
    internal static string CsvCell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static string Text(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            DateTimeOffset t => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
            TimeSpan t => System.Xml.XmlConvert.ToString(t),
            string[] tags => string.Join(' ', tags),
            JsonElement e => e.GetRawText(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case string[] tags:
                writer.WriteStartArray();
                foreach (string tag in tags)
                {
                    writer.WriteStringValue(tag);
                }

                writer.WriteEndArray();
                break;
            case JsonElement e:
                e.WriteTo(writer);
                break;
            default:
                writer.WriteStringValue(Text(value));   // decimals as exact strings, timestamps as RFC 3339 UTC (doc 14 §9.1)
                break;
        }
    }

    private static void WriteColumns(Utf8JsonWriter writer, IReadOnlyList<QueryColumn> columns)
    {
        writer.WriteStartArray("columns");
        foreach (QueryColumn column in columns)
        {
            writer.WriteStartObject();
            writer.WriteString("name", column.Name);
            writer.WriteString("type", column.Type.ToString().ToLowerInvariant());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteConsistency(Utf8JsonWriter writer, IReadOnlyList<QuerySourceConsistency> sources)
    {
        writer.WriteStartObject("consistency");
        writer.WriteStartArray("sources");
        foreach (QuerySourceConsistency source in sources)
        {
            writer.WriteStartObject();
            writer.WriteString("source", source.Source);
            writer.WriteString("level", source.Level);
            writer.WriteBoolean("stale", source.Stale);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteFields(Utf8JsonWriter writer, IReadOnlyList<QueryField> fields)
    {
        writer.WriteStartArray("fields");
        foreach (QueryField field in fields)
        {
            writer.WriteStartObject();
            writer.WriteString("name", field.Name);
            writer.WriteString("type", field.Type.ToString().ToLowerInvariant());
            writer.WriteBoolean("queryable", field.Queryable);
            writer.WriteBoolean("personalData", field.PersonalData);
            if (field.Fields.Count > 0)
            {
                WriteFields(writer, field.Fields);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteDiagnostics(Utf8JsonWriter writer, IReadOnlyList<QueryDiagnostic> diagnostics)
    {
        writer.WriteStartArray("diagnostics");
        foreach (QueryDiagnostic diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString("message", diagnostic.Message);
            writer.WriteNumber("line", diagnostic.Line);
            writer.WriteNumber("column", diagnostic.Column);
            writer.WriteNumber("length", diagnostic.Length);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // RFC 9457 problem details with codes and positions; never the caller's text (S-3).
    // A domain query's own policy (doc 14 §9.1 "per-query policy"), on top of the endpoint group's.
    private static async Task<bool> AuthorizedAsync(HttpContext http, DomainQueryDescriptor query)
    {
        if (query.Policy is not { } policy)
        {
            return true;
        }

        AuthorizationResult result = await http.RequestServices.GetRequiredService<IAuthorizationService>().AuthorizeAsync(http.User, policy).ConfigureAwait(false);
        return result.Succeeded;
    }

    private static Task ProblemAsync(HttpContext http, int status, string title, string? code, IReadOnlyList<QueryDiagnostic> diagnostics)
    {
        http.Response.ContentType = "application/problem+json";
        return WriteJsonAsync(http, status, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "https://sourceflow.net/problems/" + (status switch
            {
                StatusCodes.Status403Forbidden => "query-forbidden",
                StatusCodes.Status429TooManyRequests or StatusCodes.Status503ServiceUnavailable => "query-throttled",
                StatusCodes.Status504GatewayTimeout => "query-timeout",
                _ => "query-invalid",
            }));
            writer.WriteString("title", title);
            writer.WriteNumber("status", status);
            if (code is not null)
            {
                writer.WriteString("code", code);
            }

            WriteDiagnostics(writer, diagnostics);
            writer.WriteEndObject();
        }, problem: true);
    }

    private static Task ThrottledAsync(HttpContext http, int status, string code, int? retryAfter)
    {
        if (retryAfter is { } seconds)
        {
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return ProblemAsync(http, status, "Too many queries; retry later.", code, []);
    }

    private static int StatusOf(string code)
    {
        return code switch
        {
            QueryCodes.PermissionDenied => StatusCodes.Status403Forbidden,
            QueryCodes.Continuation => StatusCodes.Status409Conflict,
            QueryCodes.ConsistencyTimeout => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status400BadRequest,
        };
    }

    private static async Task WriteJsonAsync(HttpContext http, int status, Action<Utf8JsonWriter> write, bool problem = false)
    {
        http.Response.StatusCode = status;
        if (!problem)
        {
            http.Response.ContentType = "application/json";
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        await http.Response.Body.WriteAsync(buffer.WrittenMemory, http.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteLineAsync(HttpContext http, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        buffer.Write("\n"u8);
        await http.Response.Body.WriteAsync(buffer.WrittenMemory, http.RequestAborted).ConfigureAwait(false);
    }

    private static string PrincipalKey(ClaimsPrincipal user)
    {
        return user.Identity?.Name ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? "anonymous";
    }
}
