using System.Text.RegularExpressions;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
string serviceUrl = builder.Configuration["Studio:ServiceUrl"]
    ?? throw new InvalidOperationException("Set Studio__ServiceUrl to the service's base URL.");
builder.Services.AddHttpClient("service", client =>
{
    client.BaseAddress = new Uri(serviceUrl.EndsWith('/') ? serviceUrl : serviceUrl + "/");
    client.Timeout = TimeSpan.FromSeconds(60);
});

WebApplication app = builder.Build();

app.Use(async (http, next) =>
{
    // Results are rendered as text only (doc 14 S-2, S-5); nothing is loaded from other origins.
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
    http.Response.Headers.CacheControl = "no-store";
    await next(http);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.Map("/api/{**path}", (RequestDelegate)Proxy.ForwardAsync);
app.MapGet("/studio/health", (RequestDelegate)(http => http.Response.WriteAsync("ok", http.RequestAborted)));
app.Run();

/// <summary>A same-origin proxy to the service, limited to the endpoints the console uses.</summary>
internal static partial class Proxy
{
    private static readonly string[] ForwardedRequestHeaders = ["Idempotency-Key", "If-Match"];
    private static readonly string[] ForwardedResponseHeaders = ["ETag", "SourceFlow-Consistency"];

    public static async Task ForwardAsync(HttpContext http)
    {
        string path = http.Request.RouteValues["path"] as string ?? string.Empty;
        if (!Allowed().IsMatch(path) || path.Contains("..", StringComparison.Ordinal))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        HttpClient client = http.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("service");
        using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), path + http.Request.QueryString);
        if (HttpMethods.IsPost(http.Request.Method))
        {
            request.Content = new StreamContent(http.Request.Body);
            if (http.Request.ContentType is { } contentType)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        foreach (string header in ForwardedRequestHeaders)
        {
            if (http.Request.Headers.TryGetValue(header, out Microsoft.Extensions.Primitives.StringValues value))
            {
                request.Headers.TryAddWithoutValidation(header, (IEnumerable<string>)value!);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
        }
        catch (HttpRequestException)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            http.Response.ContentType = "application/problem+json";
            await http.Response.WriteAsync("{\"title\":\"The service is not reachable.\",\"status\":502}", http.RequestAborted);
            return;
        }

        using (response)
        {
            http.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } type)
            {
                http.Response.ContentType = type.ToString();
            }

            foreach (string header in ForwardedResponseHeaders)
            {
                if (response.Headers.TryGetValues(header, out IEnumerable<string>? values))
                {
                    http.Response.Headers[header] = values.ToArray();
                }
            }

            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        }
    }

    [GeneratedRegex("^(health|query|query/schema|query/validate|accounts/[0-9a-fA-F-]{32,36}(/(deposits|withdrawals|close))?|transfers/[0-9a-fA-F-]{32,36})$", RegexOptions.CultureInvariant)]
    private static partial Regex Allowed();
}
