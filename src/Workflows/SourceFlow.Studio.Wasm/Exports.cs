using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using SourceFlow.Studio.Dsl.Api;

namespace SourceFlow.Studio.Wasm;

/// <summary>
/// The JavaScript exports: one per <see cref="StudioApi"/> method, same name, one JSON string in and out (ADR-025).
/// <c>@sourceflow/core-client</c> calls them from a Web Worker; its <c>exports.d.ts</c> is generated from these signatures.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class Exports
{
    /// <summary>The API and DSL versions.</summary>
    [JSExport]
    public static string Version(string request) => StudioApi.Version(request);

    /// <summary>Reads a workflow.</summary>
    [JSExport]
    public static string Parse(string request) => StudioApi.Parse(request);

    /// <summary>Reads and validates a workflow against the domain's manifests.</summary>
    [JSExport]
    public static string Validate(string request) => StudioApi.Validate(request);

    /// <summary>One simulator move.</summary>
    [JSExport]
    public static string SimulateStep(string request) => StudioApi.SimulateStep(request);

    /// <summary>Every distinct path of a workflow.</summary>
    [JSExport]
    public static string EnumeratePaths(string request) => StudioApi.EnumeratePaths(request);

    /// <summary>The Mermaid statechart.</summary>
    [JSExport]
    public static string ToMermaid(string request) => StudioApi.ToMermaid(request);

    /// <summary>The BPMN 2.0 XML.</summary>
    [JSExport]
    public static string ToBpmn(string request) => StudioApi.ToBpmn(request);

    /// <summary>WorkflowSpec tests for the paths.</summary>
    [JSExport]
    public static string GenerateSpecs(string request) => StudioApi.GenerateSpecs(request);

    /// <summary>The statechart: nodes and kinded edges.</summary>
    [JSExport]
    public static string Graph(string request) => StudioApi.Graph(request);

    /// <summary>The swimlane sequence of one path.</summary>
    [JSExport]
    public static string Sequence(string request) => StudioApi.Sequence(request);
}
