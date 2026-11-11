namespace SourceFlow;

/// <summary>Gives a message type its stable logical name and version (ADR-005, DM-R1).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MessageTypeAttribute : Attribute
{
    /// <summary>Declares the logical type name and version.</summary>
    /// <param name="name">Lowercase, dot-separated, unique within the deployment.</param>
    /// <param name="version">Starts at 1.</param>
    public MessageTypeAttribute(string name, int version = 1)
    {
        Name = name;
        Version = version;
    }

    /// <summary>The logical type name, for example <c>bank.account.deposited</c>.</summary>
    public string Name { get; }

    /// <summary>The schema version, starting at 1.</summary>
    public int Version { get; }
}

/// <summary>Marks a <c>sealed partial record</c> as an aggregate (ADR-003).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AggregateAttribute : Attribute
{
    /// <summary>Declares the aggregate (stream type) name.</summary>
    public AggregateAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The aggregate name; stream ids are <c>{name}-{id}</c>.</summary>
    public string Name { get; }

    /// <summary>Write a snapshot every N events; 0 (the default) disables snapshots (doc 05 §8.1).</summary>
    public int SnapshotEvery { get; set; }

    /// <summary>Bump to invalidate snapshots when <c>When</c> logic changes without a shape change.</summary>
    public int SnapshotVersion { get; set; } = 1;
}

/// <summary>Marks a projection (ADR-016).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ProjectionAttribute : Attribute
{
    /// <summary>Declares the projection name.</summary>
    public ProjectionAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The projection (and collection) name.</summary>
    public string Name { get; }

    /// <summary>Bumping the version triggers a blue/green rebuild (doc 06 §6). Defaults to 1.</summary>
    public int Version { get; set; } = 1;
}

/// <summary>Marks a process manager (ADR-020).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ProcessManagerAttribute : Attribute
{
    /// <summary>Declares the process manager name (optionally with <c>@version</c>).</summary>
    public ProcessManagerAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The process type name.</summary>
    public string Name { get; }
}

/// <summary>
/// Marks the module class of an assembly. The generator fills in its registration (no assembly scanning, GAP-16).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SourceFlowModuleAttribute : Attribute
{
    /// <summary>Declares the module and the assembly's source-generated JSON context (DC-1).</summary>
    /// <param name="jsonContext">A <c>JsonSerializerContext</c> listing every message, document and state type.</param>
    public SourceFlowModuleAttribute(Type? jsonContext = null)
    {
        JsonContext = jsonContext;
    }

    /// <summary>The module's JSON serializer context type.</summary>
    public Type? JsonContext { get; }
}

/// <summary>Disambiguates the target aggregate id when a command has several members of the id type (DM-R3).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field, Inherited = false)]
public sealed class AggregateIdAttribute : Attribute;

/// <summary>
/// Marks a member as personal data: encrypted per subject and erasable (doc 10 §4), and classified as
/// <see cref="SourceFlowTaxonomy.PersonalData"/> so logs and telemetry redact it (doc 10 §5).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field, Inherited = false)]
public sealed class PersonalDataAttribute : Microsoft.Extensions.Compliance.Classification.DataClassificationAttribute
{
    /// <summary>Marks the member.</summary>
    public PersonalDataAttribute()
        : base(SourceFlowTaxonomy.PersonalData)
    {
    }
}

/// <summary>Marks a member as a secret (tokens, keys): always redacted in logs and telemetry (doc 10 §5).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field, Inherited = false)]
public sealed class SecretAttribute : Microsoft.Extensions.Compliance.Classification.DataClassificationAttribute
{
    /// <summary>Marks the member.</summary>
    public SecretAttribute()
        : base(SourceFlowTaxonomy.Secret)
    {
    }
}

/// <summary>Makes a member indexed and filterable in SFQL (DM-R6).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field, Inherited = false)]
public sealed class QueryableAttribute : Attribute;

/// <summary>Declares a DCB tag on a member (ADR-021).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field, Inherited = false)]
public sealed class TagAttribute : Attribute
{
    /// <summary>Declares the tag name.</summary>
    public TagAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The tag name, for example <c>account</c>.</summary>
    public string Name { get; }
}

/// <summary>Keeps queries stable when a member is renamed between versions (DM-R6).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field, AllowMultiple = true, Inherited = false)]
public sealed class RenamedFromAttribute : Attribute
{
    /// <summary>Declares the previous member name.</summary>
    public RenamedFromAttribute(string name)
    {
        Name = name;
    }

    /// <summary>The previous name.</summary>
    public string Name { get; }

    /// <summary>The message version that still used the old name.</summary>
    public int Version { get; set; }
}

/// <summary>Declares an event type a <c>Decide</c> can produce when static analysis can't (SF0019).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class EmitsAttribute : Attribute
{
    /// <summary>Declares the emitted event type.</summary>
    public EmitsAttribute(Type eventType)
    {
        EventType = eventType;
    }

    /// <summary>The emitted event type.</summary>
    public Type EventType { get; }
}

/// <summary>Declares a rejection code a <c>Decide</c> can return when static analysis can't (SF0019).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class RejectsAttribute : Attribute
{
    /// <summary>Declares the rejection code.</summary>
    public RejectsAttribute(string code)
    {
        Code = code;
    }

    /// <summary>The rejection error code.</summary>
    public string Code { get; }
}
