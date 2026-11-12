using System.Diagnostics;

namespace SourceFlow.Diagnostics;

/// <summary>Which OpenTelemetry messaging semantic conventions SourceFlow emits (<see cref="SemanticConventions.OptInVariable"/>).</summary>
public enum SemanticConventionsMode
{
    /// <summary>The pinned version, <see cref="SemanticConventions.PinnedVersion"/>: the default, stable across SourceFlow upgrades.</summary>
    Pinned,

    /// <summary><c>messaging</c>: the newest messaging conventions this SourceFlow release knows (<see cref="SemanticConventions.LatestVersion"/>).</summary>
    Latest,

    /// <summary>
    /// <c>messaging/dup</c>: the newest conventions plus the deprecated attribute names of older ones, for moving
    /// dashboards and alerts from one to the other.
    /// </summary>
    Duplicate,
}

/// <summary>
/// The OpenTelemetry semantic conventions of SourceFlow's spans and metrics (ADR-019, doc 08 §2.1). Attribute names are
/// pinned to <see cref="PinnedVersion"/>, so a SourceFlow upgrade never renames what dashboards query. A newer version
/// is adopted only when <c>OTEL_SEMCONV_STABILITY_OPT_IN</c> lists <c>messaging</c> (or <c>messaging/dup</c> for both
/// the new and the deprecated names), following the OpenTelemetry guidance for instrumentation libraries.
/// </summary>
public static class SemanticConventions
{
    /// <summary>The environment variable that opts in to newer conventions (a comma-separated list).</summary>
    public const string OptInVariable = "OTEL_SEMCONV_STABILITY_OPT_IN";

    /// <summary>The semantic conventions version SourceFlow emits by default.</summary>
    public const string PinnedVersion = "1.27.0";

    /// <summary>The newest messaging conventions this release knows; emitted only after the opt-in.</summary>
    public const string LatestVersion = "1.27.0";

    /// <summary>
    /// The deprecated operation attribute of conventions before 1.26, emitted alongside <c>messaging.operation.type</c>
    /// with <see cref="SemanticConventionsMode.Duplicate"/>.
    /// </summary>
    public const string LegacyOperation = "messaging.operation";

    /// <summary>The mode of this process, read from <see cref="OptInVariable"/> at startup.</summary>
    public static SemanticConventionsMode Mode { get; internal set; } = Parse(Environment.GetEnvironmentVariable(OptInVariable));

    /// <summary>The conventions version in effect.</summary>
    public static string Version => Mode == SemanticConventionsMode.Pinned ? PinnedVersion : LatestVersion;

    /// <summary>The telemetry schema URL of <see cref="Version"/>, set on the ActivitySource and the Meter.</summary>
    public static string SchemaUrl => "https://opentelemetry.io/schemas/" + Version;

    /// <summary>
    /// Reads an <c>OTEL_SEMCONV_STABILITY_OPT_IN</c> value: <c>messaging/dup</c> wins over <c>messaging</c>; other
    /// entries (http, database) are for other instrumentations and are ignored.
    /// </summary>
    public static SemanticConventionsMode Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SemanticConventionsMode.Pinned;
        }

        string[] entries = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Contains("messaging/dup", StringComparer.OrdinalIgnoreCase))
        {
            return SemanticConventionsMode.Duplicate;
        }

        return entries.Contains("messaging", StringComparer.OrdinalIgnoreCase) ? SemanticConventionsMode.Latest : SemanticConventionsMode.Pinned;
    }

    /// <summary>
    /// Sets <c>messaging.operation.type</c>, plus the deprecated <c>messaging.operation</c> with
    /// <see cref="SemanticConventionsMode.Duplicate"/> (pre-1.26 values: <c>publish</c> for send; settle had none).
    /// </summary>
    public static void SetOperationType(Activity? activity, string type)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(SourceFlowTelemetry.Tags.OperationType, type);
        if (Mode == SemanticConventionsMode.Duplicate && Legacy(type) is { } legacy)
        {
            activity.SetTag(LegacyOperation, legacy);
        }
    }

    private static string? Legacy(string type)
    {
        return type switch
        {
            "send" => "publish",
            "create" or "receive" or "process" => type,
            _ => null,
        };
    }
}
