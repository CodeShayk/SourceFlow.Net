using System.Diagnostics;
using System.Text;

namespace SourceFlow.Cloud.Tests.Topology;

/// <summary>
/// Runs Terraform for the IaC tests: the binary in <c>SOURCEFLOW_TERRAFORM</c>, <c>terraform</c> on the path (CI installs
/// it), or, with <c>SOURCEFLOW_IAC=1</c>, the pinned image in Docker (providers are large downloads). In Docker the working directory is mounted at <c>/work</c>, providers are cached in
/// the <c>sourceflow-terraform-plugins</c> volume, and the host is <c>host.docker.internal</c>.
/// </summary>
internal sealed class TerraformCli
{
    /// <summary>The pinned image (also the version CI installs).</summary>
    public const string Image = "hashicorp/terraform:1.9.8";

    private const string PluginVolume = "sourceflow-terraform-plugins";

    private readonly string? _binary;

    private TerraformCli(string? binary)
    {
        _binary = binary;
    }

    /// <summary>True when Terraform runs in a container, so emulators on the host are reached through <c>host.docker.internal</c>.</summary>
    public bool InDocker => _binary is null;

    /// <summary>A Terraform to run, or null (with the reason) when there is none.</summary>
    public static (TerraformCli? Cli, string Reason) Find()
    {
        if (Environment.GetEnvironmentVariable("SOURCEFLOW_TERRAFORM") is { Length: > 0 } configured)
        {
            return (new TerraformCli(configured), string.Empty);
        }

        if (OnPath("terraform") is { } binary)
        {
            return (new TerraformCli(binary), string.Empty);
        }

        if (!string.Equals(Environment.GetEnvironmentVariable("SOURCEFLOW_IAC"), "1", StringComparison.Ordinal))
        {
            return (null, "Terraform is not installed (SOURCEFLOW_TERRAFORM or terraform on the path); set SOURCEFLOW_IAC=1 to run it in Docker.");
        }

        return OnPath("docker") is not null && Succeeds("docker", "version --format {{.Server.Version}}")
            ? (new TerraformCli(null), string.Empty)
            : (null, "SOURCEFLOW_IAC=1 but Docker is not available to run Terraform.");
    }

    /// <summary>A URL on the host as Terraform sees it.</summary>
    public string HostUrl(string url)
    {
        return InDocker ? url.Replace("://localhost", "://host.docker.internal", StringComparison.OrdinalIgnoreCase).Replace("://127.0.0.1", "://host.docker.internal", StringComparison.Ordinal) : url;
    }

    /// <summary>Runs <c>terraform {arguments}</c> in <paramref name="directory"/>; returns the exit code and the output.</summary>
    public async Task<(int ExitCode, string Output)> RunAsync(string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (_binary is not null)
        {
            start.FileName = _binary;
            start.WorkingDirectory = directory;
            start.Environment["TF_IN_AUTOMATION"] = "1";
            if (!start.Environment.TryGetValue("TF_PLUGIN_CACHE_DIR", out string? cache) || string.IsNullOrEmpty(cache))
            {
                cache = Path.Combine(Path.GetTempPath(), "sourceflow-terraform-plugins");
                start.Environment["TF_PLUGIN_CACHE_DIR"] = cache;
            }

            Directory.CreateDirectory(cache);
        }
        else
        {
            start.FileName = "docker";
            foreach (string argument in (string[])["run", "--rm", "--add-host=host.docker.internal:host-gateway",
                "-v", $"{directory}:/work", "-w", "/work", "-v", $"{PluginVolume}:/plugins",
                "-e", "TF_PLUGIN_CACHE_DIR=/plugins", "-e", "TF_IN_AUTOMATION=1", Image])
            {
                start.ArgumentList.Add(argument);
            }
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Terraform did not start.");
        var output = new StringBuilder();
        Task stdout = PumpAsync(process.StandardOutput, output);
        Task stderr = PumpAsync(process.StandardError, output);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        await Task.WhenAll(stdout, stderr);
        return (process.ExitCode, output.ToString());
    }

    /// <summary>Runs Terraform and fails with its output on a non-zero exit code.</summary>
    public async Task<string> RunOrFailAsync(string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        (int code, string output) = await RunAsync(directory, arguments, cancellationToken);
        if (code != 0)
        {
            throw new InvalidOperationException($"terraform {string.Join(' ', arguments)} exited with {code}:{Environment.NewLine}{output}");
        }

        return output;
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder output)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (output)
            {
                output.AppendLine(line);
            }
        }
    }

    private static string? OnPath(string name)
    {
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ""] : [""];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool Succeeds(string file, string arguments)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(15_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
