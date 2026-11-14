using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Messaging;
using SourceFlow.Runtime;

namespace SourceFlow.AspNetCore;

/// <summary>HTTP header names used by command endpoints.</summary>
public static class SourceFlowHeaders
{
    /// <summary>The idempotency key request header.</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>The read-your-writes consistency token response header (doc 14 §6).</summary>
    public const string Consistency = "SourceFlow-Consistency";
}

/// <summary>Maps commands to HTTP endpoints (doc 03 §5.1).</summary>
public static class CommandEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>POST {pattern}</c> to <typeparamref name="TCommand"/>. The JSON body and the route values are bound to the
    /// command (route values fill members missing from the body; a body member that differs from its route value is a
    /// 400). <c>Idempotency-Key</c> becomes the idempotency key and
    /// <c>If-Match</c> the expected stream version. Responses: 200 with <c>ETag</c> and <c>SourceFlow-Consistency</c>,
    /// 202 when dispatched to a remote owner, 422 ProblemDetails for rejections, 409 for conflicts (412 with
    /// <c>If-Match</c>), 400 for unreadable bodies.
    /// </summary>
    public static IEndpointConventionBuilder MapCommand<TCommand>(this IEndpointRouteBuilder endpoints, string pattern)
        where TCommand : class, ICommand
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        // A RequestDelegate (not a lambda with bound parameters) keeps the endpoint free of reflection-based binding (AOT).
        return endpoints.MapPost(pattern, (RequestDelegate)HandleAsync<TCommand>)
            .WithName(typeof(TCommand).Name)
            .WithDisplayName("SourceFlow command " + typeof(TCommand).Name);
    }

    private static async Task HandleAsync<TCommand>(HttpContext http)
        where TCommand : class, ICommand
    {
        IServiceProvider services = http.RequestServices;
        RuntimeModel model = services.GetRequiredService<RuntimeModel>();
        MessageTypeInfo type = model.Registry.Get(typeof(TCommand));

        TCommand command;
        try
        {
            command = await BindAsync<TCommand>(http, type).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            await Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest, title: "sourceflow.invalid_request").ExecuteAsync(http).ConfigureAwait(false);
            return;
        }

        StreamVersion? expected = null;
        string? ifMatch = http.Request.Headers.IfMatch;
        if (!string.IsNullOrEmpty(ifMatch))
        {
            if (!long.TryParse(ifMatch.Trim().Trim('"').TrimStart('W', '/').Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out long version))
            {
                await Results.Problem("If-Match must be a stream version ETag.", statusCode: StatusCodes.Status400BadRequest, title: "sourceflow.invalid_request").ExecuteAsync(http).ConfigureAwait(false);
                return;
            }

            expected = new StreamVersion(version);
        }

        string? key = http.Request.Headers[SourceFlowHeaders.IdempotencyKey];
        var options = new SendOptions { IdempotencyKey = string.IsNullOrEmpty(key) ? null : key, ExpectedVersion = expected };
        CommandResult result = await services.GetRequiredService<ICommandSender>().SendAsync(command, options, http.RequestAborted).ConfigureAwait(false);
        await ToResult(http, result, expected is not null).ExecuteAsync(http).ConfigureAwait(false);
    }

    private static async Task<TCommand> BindAsync<TCommand>(HttpContext http, MessageTypeInfo type)
        where TCommand : class, ICommand
    {
        JsonNode? body = http.Request.ContentLength is 0 ? null : await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false);
        JsonObject json = body as JsonObject ?? (body is null ? [] : throw new JsonException("The request body must be a JSON object."));
        JsonNamingPolicy naming = type.JsonTypeInfo.Options.PropertyNamingPolicy ?? JsonNamingPolicy.CamelCase;
        // The route identifies the aggregate (doc 03 §5.1): a body member that names a different one is refused, so an
        // authorization check on the route can't be sidestepped by the body (CR-06). A missing member takes the route value.
        foreach (KeyValuePair<string, object?> route in http.Request.RouteValues)
        {
            if (route.Value is not string value)
            {
                continue;
            }

            string name = naming.ConvertName(route.Key);
            KeyValuePair<string, JsonNode?> member = json.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
            if (member.Key is null)
            {
                json[name] = value;
            }
            else if (!string.Equals(Text(member.Value), value, StringComparison.Ordinal))
            {
                throw new ArgumentException($"The body's '{member.Key}' differs from the route value '{route.Key}'.");
            }
        }

        return JsonSerializer.Deserialize(json, type.JsonTypeInfo) as TCommand
            ?? throw new JsonException($"The body could not be read as {typeof(TCommand).Name}.");

        static string? Text(JsonNode? node)
        {
            return node is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.String ? scalar.GetValue<string>() : node?.ToJsonString();
        }
    }

    private static IResult ToResult(HttpContext http, CommandResult result, bool conditional)
    {
        if (result.Token != ConsistencyToken.None)
        {
            http.Response.Headers[SourceFlowHeaders.Consistency] = result.Token.ToString();
        }

        switch (result.Status)
        {
            case CommandStatus.Accepted or CommandStatus.NoOp or CommandStatus.Duplicate:
                http.Response.Headers.ETag = "\"" + result.Version.Value.ToString(CultureInfo.InvariantCulture) + "\"";
                return Results.Json(
                    new CommandResponse(result.Status.ToString(), result.Version.Value, result.Error?.Code),
                    CommandResponseJsonContext.Default.CommandResponse);
            case CommandStatus.Dispatched:
                return Results.StatusCode(StatusCodes.Status202Accepted);
            case CommandStatus.Rejected:
                return Results.Problem(result.Error?.Message, statusCode: StatusCodes.Status422UnprocessableEntity, title: result.Error?.Code,
                    type: "urn:sourceflow:error:" + result.Error?.Code);
            default:
                return Results.Problem(result.Error?.Message ?? "The stream changed concurrently.",
                    statusCode: conditional ? StatusCodes.Status412PreconditionFailed : StatusCodes.Status409Conflict,
                    title: result.Error?.Code ?? Error.ConcurrencyConflict.Code);
        }
    }
}

/// <summary>The body of a successful command response.</summary>
/// <param name="Status">The command status.</param>
/// <param name="Version">The stream version after the command.</param>
/// <param name="Error">The error code of a duplicate of a failed request, if any.</param>
public sealed record CommandResponse(string Status, long Version, string? Error);

[System.Text.Json.Serialization.JsonSerializable(typeof(CommandResponse))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class CommandResponseJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
