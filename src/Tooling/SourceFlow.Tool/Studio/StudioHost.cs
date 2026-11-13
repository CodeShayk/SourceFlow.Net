using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Studio.Workspace;

namespace SourceFlow.Tool.Studio;

/// <summary>What <c>dotnet sourceflow studio</c> serves (doc 15 §6.3, §6.4; WP-S.8).</summary>
/// <param name="Workspace">The workspace file, or the folder that holds <c>sourceflow.domain.yaml</c>.</param>
/// <param name="Port">The loopback port; 0 picks a free one.</param>
/// <param name="ReadOnly">Refuse every write (files and manifest exports).</param>
/// <param name="AssetsDirectory">The built SPA (<c>studio/app/dist</c>); null serves the copy embedded in the tool.</param>
/// <param name="FrameworkDirectory">The WebAssembly core's <c>_framework</c> folder; null serves the embedded copy.</param>
/// <param name="ExportManifest">Runs a service's design-time manifest export (the project file → the manifest JSON).</param>
internal sealed record StudioHostOptions(
    string Workspace,
    int Port = StudioHost.DefaultPort,
    bool ReadOnly = false,
    string? AssetsDirectory = null,
    string? FrameworkDirectory = null,
    Func<string, CancellationToken, Task<string>>? ExportManifest = null);

/// <summary>
/// The Studio local host: Kestrel bound to 127.0.0.1 only, serving the SPA, the WebAssembly core and a REST API over the
/// workspace. It refuses requests whose Host header isn't a loopback name (DNS rebinding), writes from another origin
/// (cross-site requests), and writes outside the workspace's workflow and layout files (doc 15 §6.4).
/// </summary>
internal sealed class StudioHost : IAsyncDisposable
{
    public const int DefaultPort = 5180;
    public const string LayoutSuffix = ".sfflow.layout.json";

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    private readonly WebApplication _app;

    private StudioHost(WebApplication app, Uri url)
    {
        _app = app;
        Url = url;
    }

    /// <summary>Where the host listens, for example <c>http://127.0.0.1:5180/</c>.</summary>
    public Uri Url { get; }

    public static async Task<StudioHost> StartAsync(StudioHostOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        DomainWorkspace first = await DomainWorkspace.LoadAsync(options.Workspace, cancellationToken: cancellationToken);   // fail fast on a bad workspace
        string workspaceFile = first.Path;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = typeof(StudioHost).Assembly.GetName().Name });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
        WebApplication app = builder.Build();

        var assets = new Assets(options.AssetsDirectory, options.FrameworkDirectory);
        app.Use((context, next) => Guard(context, next));
        MapApi(app, workspaceFile, options);
        app.MapFallback("{**path}", context => assets.ServeAsync(context));   // after the API routes

        await app.StartAsync(cancellationToken);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new StudioHost(app, new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal).TrimEnd('/') + "/"));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // ------------------------------------------------------------------ the guard (doc 15 §6.4)

    private static async Task Guard(HttpContext context, RequestDelegate next)
    {
        HttpResponse response = context.Response;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.XFrameOptions = "DENY";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; worker-src 'self' blob:; " +
            "connect-src 'self'; img-src 'self' data: blob:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";

        if (!LoopbackHosts.Contains(context.Request.Host.Host, StringComparer.OrdinalIgnoreCase))
        {
            await Problem(context, StatusCodes.Status403Forbidden, "The Studio host answers loopback names only (localhost, 127.0.0.1); this request's Host header is '" + context.Request.Host + "'.");
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            string? origin = context.Request.Headers.Origin;
            if (origin is not null && (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? from) || !LoopbackHosts.Contains(from.Host, StringComparer.OrdinalIgnoreCase)
                || from.Port != context.Request.Host.Port))
            {
                await Problem(context, StatusCodes.Status403Forbidden, "Writes are accepted from the Studio page only (origin '" + origin + "').");
                return;
            }

            if (context.Request.ContentLength is > 0 && context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                await Problem(context, StatusCodes.Status415UnsupportedMediaType, "Send JSON (Content-Type: application/json).");
                return;
            }
        }

        await next(context);
    }

    // ------------------------------------------------------------------ the API

    private static void MapApi(WebApplication app, string workspaceFile, StudioHostOptions options)
    {
        app.MapGet("/api/workspace", async (HttpContext context) =>
        {
            DomainWorkspace workspace = await DomainWorkspace.LoadAsync(workspaceFile, cancellationToken: context.RequestAborted);
            var services = new List<ServiceDto>();
            foreach (WorkspaceService service in workspace.Services)
            {
                string manifest = await ReadManifestAsync(workspace, service, context.RequestAborted);
                services.Add(new ServiceDto(service.Name, service.ManifestLocation, manifest, service.WorkflowsLocation, service.ProjectLocation, service.WorkflowFiles));
            }

            return Results.Json(new WorkspaceDto(workspace.Domain, workspace.Description, Path.GetFileName(workspace.Path), options.ReadOnly, services), StudioJson.Default.WorkspaceDto);
        });

        app.MapGet("/api/files", async (HttpContext context, string path) =>
        {
            DomainWorkspace workspace = await DomainWorkspace.LoadAsync(workspaceFile, cancellationToken: context.RequestAborted);
            if (Resolve(workspace, path, forWrite: false) is not { } file)
            {
                return Refused(path);
            }

            return File.Exists(file)
                ? Results.Text(await File.ReadAllTextAsync(file, context.RequestAborted), "text/plain; charset=utf-8")
                : Results.Json(new ProblemDto("No file '" + path + "'."), StudioJson.Default.ProblemDto, statusCode: StatusCodes.Status404NotFound);
        });

        app.MapPut("/api/files", async (HttpContext context, string path) =>
        {
            if (options.ReadOnly)
            {
                return ReadOnly();
            }

            DomainWorkspace workspace = await DomainWorkspace.LoadAsync(workspaceFile, cancellationToken: context.RequestAborted);
            if (Resolve(workspace, path, forWrite: true) is not { } file)
            {
                return Refused(path);
            }

            FileWriteDto? body = await context.Request.ReadFromJsonAsync(StudioJson.Default.FileWriteDto, context.RequestAborted);
            if (body?.Content is not { } content)
            {
                return Results.Json(new ProblemDto("The body is {\"content\": \"…\"}."), StudioJson.Default.ProblemDto, statusCode: StatusCodes.Status400BadRequest);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temporary = file + ".tmp-" + Path.GetRandomFileName();
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), context.RequestAborted);
            File.Move(temporary, file, overwrite: true);
            return Results.Json(new FileWrittenDto(path, Encoding.UTF8.GetByteCount(content)), StudioJson.Default.FileWrittenDto);
        });

        app.MapPost("/api/services/{service}/manifest/export", async (HttpContext context, string service) =>
        {
            if (options.ReadOnly)
            {
                return ReadOnly();
            }

            DomainWorkspace workspace = await DomainWorkspace.LoadAsync(workspaceFile, cancellationToken: context.RequestAborted);
            if (workspace.Service(service) is not { } entry)
            {
                return Results.Json(new ProblemDto("No service '" + service + "' in the workspace."), StudioJson.Default.ProblemDto, statusCode: StatusCodes.Status404NotFound);
            }

            if (entry.ProjectLocation is not { } project || options.ExportManifest is not { } export)
            {
                return Results.Json(new ProblemDto("Service '" + service + "' has no 'project' in " + Path.GetFileName(workspace.Path) + ", so its manifest can't be exported from here."),
                    StudioJson.Default.ProblemDto, statusCode: StatusCodes.Status400BadRequest);
            }

            string target = Path.GetFullPath(Path.Combine(workspace.Root, entry.ManifestLocation));
            if (!Inside(workspace.Root, target))
            {
                return Refused(entry.ManifestLocation);
            }

            string json = await export(Path.GetFullPath(Path.Combine(workspace.Root, project)), context.RequestAborted);
            await File.WriteAllTextAsync(target, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), context.RequestAborted);
            return Results.Json(new ExportedDto(service, entry.ManifestLocation), StudioJson.Default.ExportedDto);
        });

        app.Map("/api/{**rest}", (string? rest) => Results.Json(new ProblemDto("No API '/api/" + rest + "'."), StudioJson.Default.ProblemDto, statusCode: StatusCodes.Status404NotFound));
    }

    private static async Task<string> ReadManifestAsync(DomainWorkspace workspace, WorkspaceService service, CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(Path.Combine(workspace.Root, service.ManifestLocation));
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "{}";
    }

    /// <summary>
    /// The full path of a workspace file the API may touch, or null: a <c>*.sfflow.yaml</c> or <c>*.sfflow.layout.json</c> inside
    /// the workspace folder (writes: inside a service's workflows folder), not through a link.
    /// </summary>
    internal static string? Resolve(DomainWorkspace workspace, string? relative, bool forWrite)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        if (!relative.EndsWith(DomainWorkspace.WorkflowSuffix, StringComparison.OrdinalIgnoreCase) && !relative.EndsWith(LayoutSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string full = Path.GetFullPath(Path.Combine(workspace.Root, relative));
        if (!Inside(workspace.Root, full) || ThroughLink(workspace.Root, full))
        {
            return null;
        }

        if (forWrite && !workspace.Services.Any(s => s.WorkflowsLocation is { } folder && Inside(Path.GetFullPath(Path.Combine(workspace.Root, folder)), full)))
        {
            return null;
        }

        return full;
    }

    private static bool Inside(string root, string full)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    // A symbolic link or junction on the way could lead outside the workspace.
    private static bool ThroughLink(string root, string full)
    {
        string stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (string? current = full; current is not null && current.Length > stop.Length; current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static IResult Refused(string? path)
    {
        return Results.Json(new ProblemDto("The Studio host reads and writes only workflow (*.sfflow.yaml) and layout (*.sfflow.layout.json) files inside the workspace; refused '" + path + "'."),
            StudioJson.Default.ProblemDto, statusCode: StatusCodes.Status403Forbidden);
    }

    private static IResult ReadOnly()
    {
        return Results.Json(new ProblemDto("The Studio host runs with --read-only."), StudioJson.Default.ProblemDto, statusCode: StatusCodes.Status403Forbidden);
    }

    private static Task Problem(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ProblemDto(message), StudioJson.Default.ProblemDto, cancellationToken: context.RequestAborted);
    }

    // ------------------------------------------------------------------ static assets

    /// <summary>The SPA and the WebAssembly core, from folders or from the tool's embedded resources.</summary>
    private sealed class Assets(string? appDirectory, string? frameworkDirectory)
    {
        private const string AppPrefix = "studio/app/";
        private const string FrameworkPrefix = "studio/framework/";

        private static readonly FileExtensionContentTypeProvider Types = CreateTypes();
        private static readonly Lazy<Dictionary<string, string>> Embedded = new(() =>
            typeof(Assets).Assembly.GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.Ordinal));

        public async Task ServeAsync(HttpContext context)
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            string path = (context.Request.Path.Value ?? "/").TrimStart('/');
            bool framework = path.StartsWith("_framework/", StringComparison.Ordinal);
            string relative = framework ? path["_framework/".Length..] : path.Length == 0 ? "index.html" : path;
            if (relative.Contains("..", StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            string? directory = framework ? frameworkDirectory : appDirectory;
            string prefix = framework ? FrameworkPrefix : AppPrefix;
            bool brotli = framework && context.Request.Headers.AcceptEncoding.ToString().Contains("br", StringComparison.Ordinal);
            if (await TryServeAsync(context, directory, prefix, relative, brotli))
            {
                return;
            }

            // Client-side routes get the SPA: paths without an extension, and page navigations (a workflow route ends in .sfflow.yaml).
            bool navigation = context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
            if (!framework && !relative.StartsWith("assets/", StringComparison.Ordinal) && (!Path.HasExtension(relative) || navigation)
                && await TryServeAsync(context, directory, prefix, "index.html", brotli: false))
            {
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            if (relative == "index.html")
            {
                await context.Response.WriteAsync("The Studio app isn't in this build: run 'npm run build' in studio/ and pass --assets studio/app/dist.", context.RequestAborted);
            }
        }

        private static async Task<bool> TryServeAsync(HttpContext context, string? directory, string prefix, string relative, bool brotli)
        {
            Stream? content = null;
            bool compressed = false;
            if (directory is not null)
            {
                string full = Path.GetFullPath(Path.Combine(directory, relative));
                if (!full.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (brotli && File.Exists(full + ".br"))
                {
                    content = File.OpenRead(full + ".br");
                    compressed = true;
                }
                else if (File.Exists(full))
                {
                    content = File.OpenRead(full);
                }
            }
            else
            {
                Assembly assembly = typeof(Assets).Assembly;
                if (brotli && Embedded.Value.TryGetValue(prefix + relative + ".br", out string? packed))
                {
                    content = assembly.GetManifestResourceStream(packed);
                    compressed = true;
                }
                else if (Embedded.Value.TryGetValue(prefix + relative, out string? name))
                {
                    content = assembly.GetManifestResourceStream(name);
                }
            }

            if (content is null)
            {
                return false;
            }

            await using (content)
            {
                context.Response.ContentType = Types.TryGetContentType(relative, out string? type) ? type : "application/octet-stream";
                if (compressed)
                {
                    context.Response.Headers.ContentEncoding = "br";
                    context.Response.Headers.Vary = "Accept-Encoding";
                }

                context.Response.Headers.CacheControl = relative == "index.html" ? "no-cache" : "public, max-age=3600";
                if (HttpMethods.IsGet(context.Request.Method))
                {
                    await content.CopyToAsync(context.Response.Body, context.RequestAborted);
                }
            }

            return true;
        }

        private static FileExtensionContentTypeProvider CreateTypes()
        {
            var types = new FileExtensionContentTypeProvider();
            types.Mappings[".wasm"] = "application/wasm";
            types.Mappings[".dat"] = "application/octet-stream";
            types.Mappings[".mjs"] = "text/javascript";
            types.Mappings[".js"] = "text/javascript";
            return types;
        }
    }
}

internal sealed record WorkspaceDto(string Domain, string? Description, string File, bool ReadOnly, IReadOnlyList<ServiceDto> Services);

internal sealed record ServiceDto(string Name, string Manifest, string ManifestJson, string? Workflows, string? Project, IReadOnlyList<string> WorkflowFiles);

internal sealed record FileWriteDto(string? Content);

internal sealed record FileWrittenDto(string Path, int Bytes);

internal sealed record ExportedDto(string Service, string Manifest);

internal sealed record ProblemDto(string Error);

[JsonSerializable(typeof(WorkspaceDto))]
[JsonSerializable(typeof(FileWriteDto))]
[JsonSerializable(typeof(FileWrittenDto))]
[JsonSerializable(typeof(ExportedDto))]
[JsonSerializable(typeof(ProblemDto))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class StudioJson : JsonSerializerContext;
