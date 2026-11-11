namespace SourceFlow;

/// <summary>The backing value of a strongly typed id (ADR-015).</summary>
public enum IdBacking
{
    /// <summary>A <see cref="System.Guid"/>; <c>New()</c> creates UUIDv7 values.</summary>
    Guid = 0,

    /// <summary>A <see cref="string"/> with a maximum length.</summary>
    String = 1,

    /// <summary>A <see cref="long"/>.</summary>
    Int64 = 2,
}

/// <summary>
/// Marks a <c>readonly partial record struct</c> with one positional parameter named <c>Value</c> as a strongly typed id.
/// The generator emits <c>New()</c>, parsing, formatting, a JSON converter and <c>ToStreamSegment()</c> (ADR-015).
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class StronglyTypedIdAttribute : Attribute
{
    /// <summary>The backing type. Must match the type of the <c>Value</c> parameter.</summary>
    public IdBacking Backing { get; set; } = IdBacking.Guid;

    /// <summary>Maximum length for <see cref="IdBacking.String"/> ids (default 64).</summary>
    public int MaxLength { get; set; } = 64;
}

/// <summary>A strongly typed id.</summary>
public interface IStronglyTypedId
{
    /// <summary>True for the default value, which is never a valid target (GAP-20).</summary>
    bool IsDefault { get; }

    /// <summary>The id as it appears in a stream id: <c>{aggregate}-{segment}</c>. Contains no dash.</summary>
    string ToStreamSegment();
}

/// <summary>A strongly typed id with static parsing, implemented by generated code.</summary>
/// <typeparam name="TSelf">The id type.</typeparam>
public interface IStronglyTypedId<TSelf> : IStronglyTypedId
    where TSelf : struct, IStronglyTypedId<TSelf>
{
    /// <summary>Parses a value written by <see cref="object.ToString"/> or <see cref="IStronglyTypedId.ToStreamSegment"/>.</summary>
    static abstract TSelf Parse(string value);

    /// <summary>Tries to parse a value written by <see cref="object.ToString"/> or <see cref="IStronglyTypedId.ToStreamSegment"/>.</summary>
    static abstract bool TryParse(string? value, out TSelf id);
}
