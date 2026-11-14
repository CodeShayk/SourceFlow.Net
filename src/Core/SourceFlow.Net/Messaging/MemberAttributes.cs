using System.Text.Json.Serialization.Metadata;

namespace SourceFlow.Messaging;

/// <summary>
/// The attributes of a serialized member. On a positional record, <c>[PersonalData] string GivenName</c> applies to the
/// constructor parameter, not to the property the compiler generates, so both are read: the property's attributes, then
/// those of the constructor parameter the serializer binds to it (doc 10 §4 writes it that way). Every reader of
/// <c>[PersonalData]</c>, <c>[Secret]</c> and <c>[Queryable]</c> goes through here, so the forms behave the same.
/// </summary>
internal static class MemberAttributes
{
    /// <summary>The member's attributes of type <typeparamref name="T"/>: the property's first, then the parameter's.</summary>
    public static IEnumerable<T> Of<T>(JsonPropertyInfo property)
        where T : Attribute
    {
        ArgumentNullException.ThrowIfNull(property);
        object[] own = property.AttributeProvider?.GetCustomAttributes(typeof(T), inherit: true) ?? [];
        object[] parameter = property.AssociatedParameter?.AttributeProvider?.GetCustomAttributes(typeof(T), inherit: true) ?? [];
        return own.Concat(parameter).OfType<T>();
    }

    /// <summary>The member's first attribute of type <typeparamref name="T"/>, or null.</summary>
    public static T? First<T>(JsonPropertyInfo property)
        where T : Attribute
    {
        return Of<T>(property).FirstOrDefault();
    }

    /// <summary>True when the member or its constructor parameter carries <typeparamref name="T"/>.</summary>
    public static bool IsDefined<T>(JsonPropertyInfo property)
        where T : Attribute
    {
        return First<T>(property) is not null;
    }
}
