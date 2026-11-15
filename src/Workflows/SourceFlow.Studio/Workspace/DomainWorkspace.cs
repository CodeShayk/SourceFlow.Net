using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SourceFlow.Studio.Dsl.Manifests;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SourceFlow.Studio.Workspace;

/// <summary>
/// A business domain workspace (doc 15 §4): the services of one domain, from <c>sourceflow.domain.yaml</c>, with their
/// manifests and workflow files. The workspace only reads other services' manifests; workflows belong to their owner.
/// </summary>
public sealed class DomainWorkspace
{
    /// <summary>The conventional workspace file name.</summary>
    public const string FileName = "sourceflow.domain.yaml";

    /// <summary>The workflow file suffix.</summary>
    public const string WorkflowSuffix = ".sfflow.yaml";

    private DomainWorkspace(string path, string domain, string? description, IReadOnlyList<WorkspaceService> services)
    {
        Path = path;
        Domain = domain;
        Description = description;
        Services = services;
    }

    /// <summary>The full path of the workspace file.</summary>
    public string Path { get; }

    /// <summary>The folder relative paths are resolved against.</summary>
    public string Root => System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>The domain name.</summary>
    public string Domain { get; }

    /// <summary>The domain description.</summary>
    public string? Description { get; }

    /// <summary>The services, in file order.</summary>
    public IReadOnlyList<WorkspaceService> Services { get; }

    /// <summary>Every service's manifest.</summary>
    public IEnumerable<ServiceManifest> Manifests => Services.Select(s => s.Manifest);

    /// <summary>The service called <paramref name="name"/>, or null.</summary>
    public WorkspaceService? Service(string name)
    {
        return Services.FirstOrDefault(s => s.Name == name);
    }

    /// <summary>
    /// Loads <paramref name="path"/> (a <c>sourceflow.domain.yaml</c>, or a folder that holds one) and every manifest it
    /// names. Relative paths are resolved against the workspace file's folder.
    /// </summary>
    /// <exception cref="WorkspaceException">The file is invalid, or a manifest is missing or invalid.</exception>
    public static async Task<DomainWorkspace> LoadAsync(string path, WorkspaceLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= WorkspaceLoadOptions.Default;
        string file = System.IO.Path.GetFullPath(Directory.Exists(path) ? System.IO.Path.Combine(path, FileName) : path);
        if (!File.Exists(file))
        {
            throw new WorkspaceException($"Workspace file '{file}' not found.");
        }

        WorkspaceFile definition = Read(file, await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
        string root = System.IO.Path.GetDirectoryName(file)!;
        var services = new List<WorkspaceService>();
        foreach (WorkspaceFile.ServiceEntry entry in definition.Services!)
        {
            ServiceManifest manifest = await ManifestAsync(entry, root, options, cancellationToken).ConfigureAwait(false);
            if (manifest.Service is { } declared && declared != entry.Name)
            {
                throw new WorkspaceException($"Service '{entry.Name}': '{entry.Manifest}' is the manifest of service '{declared}'.");
            }

            string? workflows = entry.Workflows is null ? null : Resolve(root, entry.Workflows);
            IReadOnlyList<string> files = workflows is not null && Directory.Exists(workflows)
                ? [.. Directory.EnumerateFiles(workflows, "*" + WorkflowSuffix, SearchOption.AllDirectories)
                    .Select(f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'))
                    .Order(StringComparer.Ordinal)]
                : [];
            services.Add(new WorkspaceService(entry.Name!, entry.Manifest!, manifest, workflows is null ? null : entry.Workflows, files) { ProjectLocation = entry.Project, TestsLocation = entry.Tests, ModuleType = entry.Module });
        }

        return new DomainWorkspace(file, definition.Domain!, definition.Description, services);
    }

    /// <summary>Parses and checks a workspace file without loading its manifests.</summary>
    /// <exception cref="WorkspaceException">The file is invalid.</exception>
    internal static WorkspaceFile Read(string file, string text)
    {
        var definition = new WorkspaceFile();
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw new WorkspaceException($"{file}: the workspace file must be a mapping with 'domain' and 'services'.");
            }

            foreach ((YamlNode key, YamlNode value) in root.Children)
            {
                switch (Key(file, key))
                {
                    case "domain":
                        definition.Domain = Scalar(file, value);
                        break;
                    case "description":
                        definition.Description = Scalar(file, value);
                        break;
                    case "services":
                        definition.Services = [.. Sequence(file, value).Select(item => Service(file, item))];
                        break;
                    default:
                        throw Error(file, key, $"unknown key '{((YamlScalarNode)key).Value}' (expected domain, description or services)");
                }
            }
        }
        catch (YamlException ex)
        {
            throw new WorkspaceException($"{file}({ex.Start.Line},{ex.Start.Column}): {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(definition.Domain))
        {
            throw new WorkspaceException($"{file}: 'domain' is required.");
        }

        if (definition.Services is not { Count: > 0 })
        {
            throw new WorkspaceException($"{file}: 'services' lists no service.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (WorkspaceFile.ServiceEntry service in definition.Services)
        {
            if (string.IsNullOrWhiteSpace(service.Name) || string.IsNullOrWhiteSpace(service.Manifest))
            {
                throw new WorkspaceException($"{file}: every service needs a 'name' and a 'manifest'.");
            }

            if (!names.Add(service.Name))
            {
                throw new WorkspaceException($"{file}: service '{service.Name}' is listed twice.");
            }
        }

        return definition;
    }

    private static WorkspaceFile.ServiceEntry Service(string file, YamlNode node)
    {
        if (node is not YamlMappingNode mapping)
        {
            throw Error(file, node, "a service is a mapping with 'name', 'manifest' and optionally 'workflows', 'project', 'tests' and 'module'");
        }

        var entry = new WorkspaceFile.ServiceEntry();
        foreach ((YamlNode key, YamlNode value) in mapping.Children)
        {
            switch (Key(file, key))
            {
                case "name":
                    entry.Name = Scalar(file, value);
                    break;
                case "manifest":
                    entry.Manifest = Scalar(file, value);
                    break;
                case "workflows":
                    entry.Workflows = Scalar(file, value);
                    break;
                case "project":
                    entry.Project = Scalar(file, value);
                    break;
                case "tests":
                    entry.Tests = Scalar(file, value);
                    break;
                case "module":
                    entry.Module = Scalar(file, value);
                    break;
                default:
                    throw Error(file, key, $"unknown key '{((YamlScalarNode)key).Value}' (expected name, manifest, workflows, project, tests or module)");
            }
        }

        return entry;
    }

    private static string Key(string file, YamlNode key)
    {
        return key is YamlScalarNode { Value: { } name } ? name : throw Error(file, key, "keys must be plain text");
    }

    private static string? Scalar(string file, YamlNode value)
    {
        return value is YamlScalarNode scalar ? scalar.Value : throw Error(file, value, "expected a single value");
    }

    private static IEnumerable<YamlNode> Sequence(string file, YamlNode value)
    {
        return value is YamlSequenceNode sequence ? sequence.Children : throw Error(file, value, "expected a list");
    }

    private static WorkspaceException Error(string file, YamlNode node, string message)
    {
        return new WorkspaceException($"{file}({node.Start.Line},{node.Start.Column}): {message}.");
    }

    private static async Task<ServiceManifest> ManifestAsync(WorkspaceFile.ServiceEntry entry, string root, WorkspaceLoadOptions options, CancellationToken cancellationToken)
    {
        string text;
        string location = entry.Manifest!;
        if (Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            // Offline by default (doc 15 §6.4): remote manifests need an explicit fetcher.
            if (options.FetchRemote is not { } fetch)
            {
                throw new WorkspaceException($"Service '{entry.Name}': the manifest '{location}' is remote; remote manifests are off unless a fetcher is configured.");
            }

            text = await fetch(uri, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string path = Resolve(root, location);
            if (!File.Exists(path))
            {
                throw new WorkspaceException($"Service '{entry.Name}': manifest '{location}' not found ({path}). Run 'dotnet sourceflow manifest export --project <service>' to create it.");
            }

            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return ServiceManifest.Parse(text);
        }
        catch (FormatException ex)
        {
            throw new WorkspaceException($"Service '{entry.Name}': manifest '{location}' is invalid: {ex.Message}", ex);
        }
    }

    private static string Resolve(string root, string path)
    {
        return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(root, path));
    }
}

/// <summary>A service of the workspace and its manifest.</summary>
/// <param name="Name">The service name.</param>
/// <param name="ManifestLocation">The manifest path or URL as written in the workspace file.</param>
/// <param name="Manifest">The loaded manifest.</param>
/// <param name="WorkflowsLocation">The workflows folder as written, or null when the service owns no workflows.</param>
/// <param name="WorkflowFiles">The <c>*.sfflow.yaml</c> files, relative to the workspace root with <c>/</c> separators, sorted.</param>
public sealed record WorkspaceService(string Name, string ManifestLocation, ServiceManifest Manifest, string? WorkflowsLocation, IReadOnlyList<string> WorkflowFiles)
{
    /// <summary>The service's project (a .csproj or its folder) as written, or null: Studio can then re-export its manifest (WP-S.8).</summary>
    public string? ProjectLocation { get; init; }

    /// <summary>
    /// The service's test project folder as written, or null: Studio writes the specs it generates for the service's
    /// workflows there (WP-S.11).
    /// </summary>
    public string? TestsLocation { get; init; }

    /// <summary>The full name of the service's module type (for example <c>Orders.OrdersModule</c>), or null; the generated specs use it.</summary>
    public string? ModuleType { get; init; }
}

/// <summary>How a workspace is loaded.</summary>
public sealed class WorkspaceLoadOptions
{
    /// <summary>Local files only.</summary>
    public static WorkspaceLoadOptions Default { get; } = new();

    /// <summary>Fetches manifests given as URLs. Null (the default) refuses them: the workspace works offline.</summary>
    public Func<Uri, CancellationToken, Task<string>>? FetchRemote { get; init; }
}

/// <summary>A workspace file or one of its manifests is missing or invalid.</summary>
public sealed class WorkspaceException : Exception
{
    /// <summary>Creates the exception.</summary>
    public WorkspaceException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its cause.</summary>
    public WorkspaceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    public WorkspaceException()
    {
    }
}

/// <summary>The content of <c>sourceflow.domain.yaml</c>. Unknown keys are errors.</summary>
internal sealed class WorkspaceFile
{
    public string? Domain { get; set; }

    public string? Description { get; set; }

    public List<ServiceEntry>? Services { get; set; }

    internal sealed class ServiceEntry
    {
        public string? Name { get; set; }

        public string? Manifest { get; set; }

        public string? Workflows { get; set; }

        public string? Project { get; set; }

        public string? Tests { get; set; }

        public string? Module { get; set; }
    }
}
