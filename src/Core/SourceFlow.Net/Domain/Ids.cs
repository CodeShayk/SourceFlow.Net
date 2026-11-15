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

/// <summary>
/// Implemented by an id's generated JSON converter: the id type and how its value is written (ADR-029 §2, CR-30). The
/// schema catalog and the SFQL catalog read it from the serializer options, so both describe what the serializer writes.
/// </summary>
public interface IStronglyTypedIdConverter
{
    /// <summary>The id type.</summary>
    Type IdType { get; }

    /// <summary>How the value is written: Guid and String ids as JSON strings, Int64 ids as JSON numbers.</summary>
    IdBacking Backing { get; }
}

/// <summary>The backing of an id type, as its generated converter reports it.</summary>
public static class IdBackings
{
    /// <summary>
    /// The backing of <paramref name="idType"/>: from its converter in <paramref name="options"/>, else from
    /// <see cref="StronglyTypedIdAttribute.Backing"/>, else <see cref="IdBacking.Guid"/>.
    /// </summary>
    public static IdBacking Of(Type idType, System.Text.Json.JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(idType);
        ArgumentNullException.ThrowIfNull(options);
        foreach (System.Text.Json.Serialization.JsonConverter converter in options.Converters)
        {
            if (converter is IStronglyTypedIdConverter id && id.IdType == idType)
            {
                return id.Backing;
            }
        }

        return System.Reflection.CustomAttributeExtensions.GetCustomAttribute<StronglyTypedIdAttribute>(idType)?.Backing ?? IdBacking.Guid;
    }
}

/// <summary>A strongly typed id.</summary>
public interface IStronglyTypedId
{
    /// <summary>True for the default value, which is never a valid target (GAP-20).</summary>
    bool IsDefault { get; }

    /// <summary>
    /// True when the value can identify an aggregate (ADR-015): not the default, a positive number for Int64 ids, and for
    /// string ids non-empty, within the maximum length, with no dash, whitespace or control character.
    /// </summary>
    bool IsValid { get; }

    /// <summary>The id as it appears in a stream id: <c>{aggregate}-{segment}</c>. Contains no dash; throws for an invalid id.</summary>
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
