using System.CommandLine;
using System.Diagnostics;
using System.Text;
using SourceFlow.Hosting;

namespace SourceFlow.Tool;

// `dotnet sourceflow manifest export` (doc 15 §3.2, WP-S.2).
internal sealed partial class Cli
{
    private static readonly TimeSpan DesignTimeTimeout = TimeSpan.FromMinutes(5);

    private Command ManifestCommand()
    {
        var project = new Option<string?>("--project")
        {
            Description = "The service project (.csproj or its folder). Its configuration runs in design-time mode: the host is built but never started, so nothing connects to storage or a broker.",
        };
        var output = new Option<string?>("--output")
        {
            Description = "The manifest file. Default: <project folder>/artifacts/sourceflow.manifest.json with --project, standard output inside a service.",
        };
        var configuration = new Option<string>("--configuration", "-c") { Description = "Build configuration.", DefaultValueFactory = _ => "Debug" };
        var noBuild = new Option<bool>("--no-build") { Description = "Don't build the project first." };
        var check = new Option<bool>("--check") { Description = "Compare with the file at --output instead of writing it; fails on drift (CI)." };
        var appArgs = new Argument<string[]>("app-args")
        {
            Description = "Arguments for the application, after --; for example -- --ConnectionStrings:SourceFlow=Host=unused.",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var export = new Command("export", "Write the service manifest: the generator's static manifest merged with routes, publications and subscriptions.");
        export.Options.Add(project);
        export.Options.Add(output);
        export.Options.Add(configuration);
        export.Options.Add(noBuild);
        export.Options.Add(check);
        export.Arguments.Add(appArgs);
        export.SetAction((r, ct) => Guard(async () =>
        {
            string? target = r.GetValue(output);
            string json;
            if (r.GetValue(project) is { } path)
            {
                string csproj = ProjectFile(path);
                target ??= Path.Combine(Path.GetDirectoryName(csproj)!, "artifacts", "sourceflow.manifest.json");
                json = await DesignTimeExportAsync(csproj, r.GetValue(configuration)!, r.GetValue(noBuild), r.GetValue(appArgs) ?? [], ct);
            }
            else
            {
                json = SourceFlowManifestExporter.Export(Service("manifest export"));
            }

            if (r.GetValue(check))
            {
                return await CheckAsync(target ?? throw new CliException("--check needs --output (the committed manifest)."), json, ct);
            }

            if (target is null)
            {
                await Out.WriteAsync(json);
                return 0;
            }

            string full = Path.GetFullPath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            await Out.WriteLineAsync("Wrote " + full);
            return 0;
        }));

        var manifest = new Command("manifest", "Service manifests for SourceFlow Studio (doc 15 §3).");
        manifest.Subcommands.Add(export);
        return manifest;
    }

    private async Task<int> CheckAsync(string target, string json, CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(target);
        if (!File.Exists(full))
        {
            await Err.WriteLineAsync($"Manifest drift: {full} doesn't exist. Run 'dotnet sourceflow manifest export' and commit it.");
            return 1;
        }

        string committed = (await File.ReadAllTextAsync(full, cancellationToken)).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (committed == json)
        {
            await Out.WriteLineAsync("The manifest is up to date: " + full);
            return 0;
        }

        string[] before = committed.Split('\n');
        string[] after = json.Split('\n');
        int line = 0;
        while (line < before.Length && line < after.Length && before[line] == after[line])
        {
            line++;
        }

        await Err.WriteLineAsync($"Manifest drift: {full} differs from the regenerated manifest at line {line + 1}.");
        await Err.WriteLineAsync("  committed:   " + (line < before.Length ? before[line].Trim() : "(end of file)"));
        await Err.WriteLineAsync("  regenerated: " + (line < after.Length ? after[line].Trim() : "(end of file)"));
        await Err.WriteLineAsync("Run 'dotnet sourceflow manifest export' and commit the result.");
        return 1;
    }

    private static string ProjectFile(string path)
    {
        string full = Path.GetFullPath(path);
        if (File.Exists(full))
        {
            return full;
        }

        if (Directory.Exists(full) && Directory.GetFiles(full, "*.csproj") is [var single])
        {
            return single;
        }

        throw new CliException($"'{path}' is not a project file or a folder with exactly one .csproj.");
    }

    private static async Task<string> DesignTimeExportAsync(string csproj, string configuration, bool noBuild, string[] appArgs, CancellationToken cancellationToken)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        if (!noBuild)
        {
            ProcessResult build = await RunProcessAsync(dotnet, ["build", csproj, "-c", configuration, "-nologo", "-v", "quiet"], [], cancellationToken);
            if (build.ExitCode != 0)
            {
                throw new CliException($"Building {csproj} failed:{Environment.NewLine}{Tail(build.Output)}");
            }
        }

        string manifest = Path.Combine(Path.GetTempPath(), "sourceflow-manifest-" + Path.GetRandomFileName() + ".json");
        try
        {
            ProcessResult run = await RunProcessAsync(
                dotnet,
                ["run", "--project", csproj, "-c", configuration, "--no-build", "--no-launch-profile", "--", .. appArgs],
                [(SourceFlowDesignTime.EnvironmentVariable, "1"), (SourceFlowDesignTime.ManifestOutputVariable, manifest)],
                cancellationToken);
            if (run.ExitCode != 0)
            {
                throw new CliException($"The design-time run of {csproj} failed (exit {run.ExitCode}):{Environment.NewLine}{Tail(run.Output)}");
            }

            if (!File.Exists(manifest))
            {
                throw new CliException($"{csproj} exited without writing a manifest. It must build a generic host (Host.CreateApplicationBuilder or WebApplication.CreateBuilder) that calls AddSourceFlow.");
            }

            return await File.ReadAllTextAsync(manifest, cancellationToken);
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(string file, IEnumerable<string> arguments, IEnumerable<(string Name, string Value)> environment, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DesignTimeTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new CliException(cancellationToken.IsCancellationRequested
                ? "Cancelled."
                : $"'{file} {string.Join(' ', start.ArgumentList)}' didn't finish within {DesignTimeTimeout.TotalMinutes} minutes.");
        }

        process.WaitForExit();
        lock (output)
        {
            return new ProcessResult(process.ExitCode, output.ToString());
        }
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }

    private static string Tail(string output)
    {
        string[] lines = output.TrimEnd().Split('\n');
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - 30)));
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
