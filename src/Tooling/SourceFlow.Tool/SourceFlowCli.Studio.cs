using System.CommandLine;
using System.Diagnostics;
using SourceFlow.Studio.Workspace;
using SourceFlow.Tool.Studio;

namespace SourceFlow.Tool;

// `dotnet sourceflow studio` (doc 15 §6.3, ADR-025; WP-S.8): the local host for SourceFlow Studio.
internal sealed partial class Cli
{
    private Command StudioCommand()
    {
        var workspace = new Option<string>("--workspace") { Description = "The workspace: a sourceflow.domain.yaml, or the folder that holds one.", DefaultValueFactory = _ => DomainWorkspace.FileName };
        var port = new Option<int>("--port") { Description = "The loopback port (0 picks a free one).", DefaultValueFactory = _ => StudioHost.DefaultPort };
        var readOnly = new Option<bool>("--read-only") { Description = "Refuse every write: workflow and layout files, and manifest exports." };
        var assets = new Option<string?>("--assets") { Description = "Serve the Studio app from this folder (studio/app/dist) instead of the copy in the tool." };
        var framework = new Option<string?>("--framework") { Description = "Serve the WebAssembly core from this _framework folder instead of the copy in the tool." };
        var open = new Option<bool>("--open") { Description = "Open the Studio in the default browser." };

        var studio = new Command("studio", "Run SourceFlow Studio on this machine: the designer for the workspace's workflows, at http://127.0.0.1:<port>/ (loopback only).");
        foreach (Option option in new Option[] { workspace, port, readOnly, assets, framework, open })
        {
            studio.Options.Add(option);
        }

        studio.SetAction((r, ct) => Guard(async () =>
        {
            StudioHost host;
            try
            {
                host = await StudioHost.StartAsync(
                    new StudioHostOptions(r.GetValue(workspace)!, r.GetValue(port), r.GetValue(readOnly), r.GetValue(assets), r.GetValue(framework),
                        (project, token) => DesignTimeExportAsync(ProjectFile(project), "Debug", noBuild: false, [], token)),
                    ct);
            }
            catch (WorkspaceException e)
            {
                throw new CliException(e.Message);
            }
            catch (IOException e)
            {
                throw new CliException($"The Studio host could not listen on port {r.GetValue(port)}: {e.Message} Pass --port 0 for a free one.");
            }

            await using (host)
            {
                await Out.WriteLineAsync($"SourceFlow Studio: {host.Url}{(r.GetValue(readOnly) ? " (read-only)" : string.Empty)}. Press Ctrl+C to stop.");
                if (r.GetValue(open))
                {
                    Process.Start(new ProcessStartInfo(host.Url.ToString()) { UseShellExecute = true })?.Dispose();
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    // Ctrl+C
                }
            }

            return 0;
        }));
        return studio;
    }
}
