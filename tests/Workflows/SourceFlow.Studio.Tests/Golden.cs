using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SourceFlow.Studio.Dsl.Diagnostics;
using SourceFlow.Studio.Dsl.Validation;
using SourceFlow.Studio.Workspace;

namespace SourceFlow.Studio.Tests;

/// <summary>
/// The WF-GOLD fixtures in <c>tests/workflow-golden</c>: a commerce domain's manifests and one folder per case with
/// <c>workflow.sfflow.yaml</c> and the expected outputs. The .NET and WASM runs read the same files (plan 10 §3).
/// </summary>
internal static class Golden
{
    public const string ApproveVariable = "SOURCEFLOW_APPROVE";

    public static readonly string Root = Path.Combine(RepositoryRoot(), "tests", "workflow-golden");

    private static readonly Lazy<DomainWorkspace> Domain = new(() =>
        DomainWorkspace.LoadAsync(Path.Combine(Root, "domain")).GetAwaiter().GetResult());

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static WorkflowEnvironment Environment => new(Domain.Value.Manifests);

    public static TheoryData<string> Cases()
    {
        return new TheoryData<string>(Directory.GetDirectories(Path.Combine(Root, "cases")).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
    }

    public static string Workflow(string @case)
    {
        return File.ReadAllText(Path.Combine(Root, "cases", @case, "workflow.sfflow.yaml"));
    }

    public static string Json(IEnumerable<WorkflowDiagnostic> diagnostics)
    {
        var array = new JsonArray();
        foreach (WorkflowDiagnostic d in diagnostics)
        {
            array.Add((JsonNode)new JsonObject
            {
                ["id"] = d.Id,
                ["severity"] = d.IsError ? "error" : "warning",
                ["line"] = d.Location.Line,
                ["column"] = d.Location.Column,
                ["message"] = d.Message,
            });
        }

        return array.ToJsonString(Indented) + "\n";
    }

    /// <summary>Compares <paramref name="actual"/> with the case's expected file; with SOURCEFLOW_APPROVE=1 it writes it.</summary>
    public static void AssertMatches(string @case, string file, string actual)
    {
        string path = Path.Combine(Root, "cases", @case, file);
        string? expected = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal) : null;
        if (expected == actual)
        {
            return;
        }

        if (System.Environment.GetEnvironmentVariable(ApproveVariable) is "1" or "true")
        {
            File.WriteAllText(path, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        actual.ShouldBe(expected, $"{@case}/{file} differs; review the change and run with {ApproveVariable}=1 to approve it.");
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "SourceFlow.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("Repository root not found.");
    }
}
