using System.Globalization;
using System.Text.Json;
using SourceFlow;
using SourceFlow.Query;
using SourceFlow.Runtime;

namespace Bank.Api;

/// <summary>
/// A sample-only query endpoint for the demo console: <c>POST {prefix}</c> runs a query through <see cref="IQueryService"/>
/// and <c>GET {prefix}/schema</c> describes the catalog. The response follows doc 14 §9.1 (columns, rows, continuation,
/// consistency, queryHash; decimals as strings; RFC 9457 problems with diagnostics, never echoing the query).
/// It grants read permissions to every caller and has no authentication, so it is for local demos only.
/// </summary>
internal static class DemoQueryApi
{
    public static void MapDemoQueryApi(this IEndpointRouteBuilder endpoints, string prefix, bool personalData)
    {
        string[] granted = personalData
            ? [QueryPermission.Events, QueryPermission.ReadModels, QueryPermission.Aggregates, QueryPermission.Schema, QueryPermission.PersonalData]
            : [QueryPermission.Events, QueryPermission.ReadModels, QueryPermission.Aggregates, QueryPermission.Schema];
        QueryPermissions permissions = QueryPermissions.Of(granted);

        endpoints.MapPost(prefix, (RequestDelegate)(http => QueryAsync(http, permissions)));
        endpoints.MapPost(prefix + "/validate", (RequestDelegate)(http => ValidateAsync(http, permissions)));
        endpoints.MapGet(prefix + "/schema", (RequestDelegate)SchemaAsync);
    }

    private static async Task QueryAsync(HttpContext http, QueryPermissions permissions)
    {
        Request? request = await ReadAsync(http).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        IQueryService queries = http.RequestServices.GetRequiredService<IQueryService>();
        QueryPage page;
        try
        {
            page = await queries.QueryPageAsync(request.Query, request.Parameters, request.Continuation,
                new QueryOptions { Language = request.Language, Permissions = permissions, PageSize = request.PageSize }, http.RequestAborted).ConfigureAwait(false);
        }
        catch (QueryException e)
        {
            await ProblemAsync(http, StatusOf(e.Code), "The query is not valid.", e.Diagnostics).ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            await ProblemAsync(http, StatusCodes.Status504GatewayTimeout, "The query timed out.", []).ConfigureAwait(false);
            return;
        }

        http.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(http.Response.Body);
        writer.WriteStartObject();
        writer.WriteStartArray("columns");
        foreach (QueryColumn column in page.Columns)
        {
            writer.WriteStartObject();
            writer.WriteString("name", column.Name);
            writer.WriteString("type", Name(column.Type));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
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
        writer.WriteStartObject("consistency");
        writer.WriteStartArray("sources");
        foreach (QuerySourceConsistency source in page.Consistency)
        {
            writer.WriteStartObject();
            writer.WriteString("source", source.Source);
            writer.WriteString("level", source.Level);
            writer.WriteBoolean("stale", source.Stale);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteString("queryHash", page.QueryHash);
        writer.WriteEndObject();
    }

    private static async Task ValidateAsync(HttpContext http, QueryPermissions permissions)
    {
        Request? request = await ReadAsync(http).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        QueryValidation validation = await http.RequestServices.GetRequiredService<IQueryService>()
            .ValidateAsync(request.Query, request.Parameters, new QueryOptions { Language = request.Language, Permissions = permissions }, http.RequestAborted).ConfigureAwait(false);
        http.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(http.Response.Body);
        writer.WriteStartObject();
        writer.WriteBoolean("valid", validation.IsValid);
        WriteDiagnostics(writer, validation.Diagnostics);
        writer.WriteStartArray("columns");
        foreach (QueryColumn column in validation.Columns)
        {
            writer.WriteStartObject();
            writer.WriteString("name", column.Name);
            writer.WriteString("type", Name(column.Type));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static async Task SchemaAsync(HttpContext http)
    {
        QueryCatalog catalog = QueryCatalog.Build(http.RequestServices.GetRequiredService<RuntimeModel>());
        http.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(http.Response.Body);
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
    }

    private static void WriteFields(Utf8JsonWriter writer, IReadOnlyList<QueryField> fields)
    {
        writer.WriteStartArray("fields");
        foreach (QueryField field in fields)
        {
            writer.WriteStartObject();
            writer.WriteString("name", field.Name);
            writer.WriteString("type", Name(field.Type));
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

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case decimal d:
                writer.WriteStringValue(d.ToString(CultureInfo.InvariantCulture));   // exact (doc 14 §9.1)
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case DateTimeOffset t:
                writer.WriteStringValue(t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
                break;
            case TimeSpan t:
                writer.WriteStringValue(System.Xml.XmlConvert.ToString(t));
                break;
            case GlobalPosition p:
                writer.WriteStringValue(p.ToString());
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
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static async Task<Request?> ReadAsync(HttpContext http)
    {
        try
        {
            using JsonDocument body = await JsonDocument.ParseAsync(http.Request.Body, new JsonDocumentOptions { MaxDepth = 16 }, http.RequestAborted).ConfigureAwait(false);
            JsonElement root = body.RootElement;
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
            int? pageSize = root.TryGetProperty("pageSize", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
            return new Request(query.GetString()!, language, parameters, continuation, pageSize);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            await ProblemAsync(http, StatusCodes.Status400BadRequest, "The request body is not valid.", []).ConfigureAwait(false);
            return null;
        }
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

    private static async Task ProblemAsync(HttpContext http, int status, string title, IReadOnlyList<QueryDiagnostic> diagnostics)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/problem+json";
        await using var writer = new Utf8JsonWriter(http.Response.Body);
        writer.WriteStartObject();
        writer.WriteString("type", "https://sourceflow.net/problems/query-invalid");
        writer.WriteString("title", title);
        writer.WriteNumber("status", status);
        WriteDiagnostics(writer, diagnostics);
        writer.WriteEndObject();
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

    private static string Name(QueryType type)
    {
        return type.ToString().ToLowerInvariant();
    }

    private sealed record Request(string Query, QueryLanguage Language, IReadOnlyDictionary<string, object?> Parameters, string? Continuation, int? PageSize);
}
