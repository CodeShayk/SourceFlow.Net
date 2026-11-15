using System.Text.Json.Nodes;

namespace SourceFlow.Catalog;

/// <summary>Whether a schema change is allowed.</summary>
public enum CompatibilitySeverity
{
    /// <summary>Compatible: old and new messages still read.</summary>
    Allowed,

    /// <summary>Breaking: needs a new version (and an upcaster for events).</summary>
    Breaking,
}

/// <summary>One finding of a compatibility check.</summary>
/// <param name="Rule">The rule (SFC01…SFC10).</param>
/// <param name="Severity">Allowed or breaking.</param>
/// <param name="Type">The type.</param>
/// <param name="Version">The version.</param>
/// <param name="Field">The field path, when the finding is about a field.</param>
/// <param name="Message">What changed and what to do.</param>
public sealed record CompatibilityFinding(string Rule, CompatibilitySeverity Severity, string Type, int Version, string? Field, string Message);

/// <summary>
/// Compares a service's schemas with a baseline (the published lock file, doc 14 §2.2). The rules:
/// <list type="table">
/// <item><term>SFC01</term><description>an optional field was added to a version: allowed.</description></item>
/// <item><term>SFC02</term><description>a required field was added to a version: breaking (old messages lack it).</description></item>
/// <item><term>SFC03</term><description>a field was removed from a version: breaking.</description></item>
/// <item><term>SFC04</term><description>a field's type changed in a version: breaking.</description></item>
/// <item><term>SFC05</term><description>an optional field became required: breaking (a required field became optional: allowed).</description></item>
/// <item><term>SFC06</term><description>a new version renames a field with <c>[RenamedFrom]</c>: allowed.</description></item>
/// <item><term>SFC07</term><description>a new version drops or renames a field without <c>[RenamedFrom]</c> or an upcaster: breaking.</description></item>
/// <item><term>SFC08</term><description>a new event version without an upcaster from the previous version: breaking (stored events can't be read as the new version).</description></item>
/// <item><term>SFC09</term><description>a published version was removed: breaking (allowed for read models, whose old generation is retired).</description></item>
/// <item><term>SFC10</term><description>query or privacy flags changed (<c>[Queryable]</c>, <c>[PersonalData]</c>, tags): allowed, reported.</description></item>
/// </list>
/// </summary>
public static class SchemaCompatibility
{
    /// <summary>Checks <paramref name="current"/> against <paramref name="baseline"/>.</summary>
    public static IReadOnlyList<CompatibilityFinding> Check(SchemaCatalog baseline, SchemaCatalog current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        var findings = new List<CompatibilityFinding>();
        foreach (SchemaEntry published in baseline.Entries)
        {
            if (!current.TryGet(published.Type, published.Version, out SchemaEntry? now))
            {
                bool readModel = published.Kind == SchemaKind.ReadModel;
                findings.Add(new CompatibilityFinding("SFC09", readModel ? CompatibilitySeverity.Allowed : CompatibilitySeverity.Breaking, published.Type, published.Version, null,
                    readModel ? "The read-model generation was retired." : "A published version was removed; keep it (with an upcaster for events)."));
                continue;
            }

            if (now.Fingerprint != published.Fingerprint)
            {
                CompareVersion(published, now, findings);
            }
        }

        foreach (IGrouping<string, SchemaEntry> type in current.Entries.GroupBy(e => e.Type, StringComparer.Ordinal))
        {
            SchemaEntry[] versions = [.. type.OrderBy(e => e.Version)];
            foreach (SchemaEntry added in versions.Where(v => !baseline.TryGet(v.Type, v.Version, out _)))
            {
                SchemaEntry? previous = versions.LastOrDefault(v => v.Version < added.Version);
                if (previous is null || added.Kind is SchemaKind.ReadModel or SchemaKind.ProcessState)
                {
                    continue;   // a brand-new type, or a new read-model generation (rebuilt from events)
                }

                CompareNewVersion(previous, added, findings);
            }
        }

        return findings;
    }

    private static void CompareVersion(SchemaEntry before, SchemaEntry after, List<CompatibilityFinding> findings)
    {
        Dictionary<string, (JsonObject Schema, bool Required)> old = Fields(before.Schema);
        Dictionary<string, (JsonObject Schema, bool Required)> now = Fields(after.Schema);
        foreach ((string name, (JsonObject schema, bool required)) in now)
        {
            if (!old.TryGetValue(name, out (JsonObject Schema, bool Required) was))
            {
                findings.Add(required
                    ? new CompatibilityFinding("SFC02", CompatibilitySeverity.Breaking, after.Type, after.Version, name, "A required field was added; messages already written lack it. Make it optional or add a version.")
                    : new CompatibilityFinding("SFC01", CompatibilitySeverity.Allowed, after.Type, after.Version, name, "An optional field was added."));
                continue;
            }

            if (Shape(was.Schema) != Shape(schema))
            {
                findings.Add(new CompatibilityFinding("SFC04", CompatibilitySeverity.Breaking, after.Type, after.Version, name, $"The field's type changed from {Shape(was.Schema)} to {Shape(schema)}; add a version with an upcaster."));
            }
            else if (!was.Required && required)
            {
                findings.Add(new CompatibilityFinding("SFC05", CompatibilitySeverity.Breaking, after.Type, after.Version, name, "An optional field became required."));
            }
            else if (Flags(was.Schema) != Flags(schema))
            {
                findings.Add(new CompatibilityFinding("SFC10", CompatibilitySeverity.Allowed, after.Type, after.Version, name, $"Query or privacy flags changed ({Flags(was.Schema)} → {Flags(schema)})."));
            }
        }

        foreach (string name in old.Keys.Where(n => !now.ContainsKey(n)))
        {
            findings.Add(new CompatibilityFinding("SFC03", CompatibilitySeverity.Breaking, after.Type, after.Version, name, "A field was removed from a published version; add a version with an upcaster."));
        }
    }

    private static void CompareNewVersion(SchemaEntry previous, SchemaEntry added, List<CompatibilityFinding> findings)
    {
        Dictionary<string, (JsonObject Schema, bool Required)> old = Fields(previous.Schema);
        Dictionary<string, (JsonObject Schema, bool Required)> now = Fields(added.Schema);
        bool upcast = previous.UpcastsTo == added.Version;
        if (added.Kind == SchemaKind.Event && !upcast)
        {
            findings.Add(new CompatibilityFinding("SFC08", CompatibilitySeverity.Breaking, added.Type, added.Version, null,
                $"Event version {added.Version} has no upcaster from version {previous.Version}; stored events of version {previous.Version} can't be read as the new version."));
        }

        foreach (string name in old.Keys.Where(n => !now.ContainsKey(n)))
        {
            string? renamedTo = now.FirstOrDefault(f => f.Value.Schema["x-sourceflow-renamed-from"] is JsonObject renames
                && renames.Any(r => r.Value?.GetValue<string>() == name)).Key;
            if (renamedTo is not null)
            {
                findings.Add(new CompatibilityFinding("SFC06", CompatibilitySeverity.Allowed, added.Type, added.Version, renamedTo, $"'{name}' was renamed to '{renamedTo}' with [RenamedFrom]."));
            }
            else if (!upcast)
            {
                findings.Add(new CompatibilityFinding("SFC07", CompatibilitySeverity.Breaking, added.Type, added.Version, name,
                    $"'{name}' was dropped or renamed without [RenamedFrom] or an upcaster from version {previous.Version}."));
            }
        }
    }

    private static Dictionary<string, (JsonObject Schema, bool Required)> Fields(JsonObject schema)
    {
        var required = new HashSet<string>((schema["required"] as JsonArray)?.Select(r => r!.GetValue<string>()) ?? [], StringComparer.Ordinal);
        var fields = new Dictionary<string, (JsonObject, bool)>(StringComparer.Ordinal);
        if (schema["properties"] is JsonObject properties)
        {
            foreach ((string name, JsonNode? value) in properties)
            {
                if (value is JsonObject field)
                {
                    fields[name] = (field, required.Contains(name));
                }
            }
        }

        return fields;
    }

    // The type-relevant part of a field schema (extensions excluded).
    private static string Shape(JsonObject field)
    {
        var shape = new JsonObject();
        foreach ((string key, JsonNode? value) in field)
        {
            if (!key.StartsWith("x-sourceflow", StringComparison.Ordinal))
            {
                shape[key] = value?.DeepClone();
            }
        }

        return JsonSchemaGenerator.Canonical(shape);
    }

    private static string Flags(JsonObject field)
    {
        return string.Join(",", field.Where(p => p.Key is "x-sourceflow-queryable" or "x-sourceflow-personal-data" or "x-sourceflow-tag")
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key["x-sourceflow-".Length..] + "=" + p.Value?.ToJsonString()));
    }
}
