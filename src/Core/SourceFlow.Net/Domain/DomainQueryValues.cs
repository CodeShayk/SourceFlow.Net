using System.Globalization;
using System.Text.Json;

namespace SourceFlow;

/// <summary>A result row as domain code sees it (doc 14 Q-B4): values by column name.</summary>
public interface IQueryRow
{
    /// <summary>Gets a column's value by name (case-insensitive).</summary>
    bool TryGetColumn(string name, out object? value);
}

/// <summary>Converts query values to result members (used by generated <c>Materialize</c> code).</summary>
public static class DomainQueryValues
{
    /// <summary>The value of a column, matched by name case-insensitively.</summary>
    public static object? Column(IQueryRow row, string name)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.TryGetColumn(name, out object? value) ? value : throw new Query.DomainQueryException($"The result has no column '{name}'.");
    }

    /// <summary>Converts a query value to a primitive member type (string, numbers, bool, times, Guid, JsonElement and their nullable forms).</summary>
    public static T Get<T>(object? value)
    {
        Type target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (value is null)
        {
            return default!;
        }

        object converted = target switch
        {
            _ when target == typeof(string) => value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture)!,
            _ when target == typeof(int) => checked((int)Integer(value)),
            _ when target == typeof(long) => Integer(value),
            _ when target == typeof(short) => checked((short)Integer(value)),
            _ when target == typeof(decimal) => value is long l ? l : Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            _ when target == typeof(double) => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            _ when target == typeof(bool) => value is bool b ? b : bool.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
            _ when target == typeof(DateTimeOffset) => value is DateTimeOffset t ? t : DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
            _ when target == typeof(DateTime) => value is DateTimeOffset dt ? dt.UtcDateTime : DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
            _ when target == typeof(Guid) => value is Guid g ? g : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
            _ when target == typeof(TimeSpan) => value is TimeSpan s ? s : TimeSpan.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
            _ when target == typeof(JsonElement) => value is JsonElement e ? e : JsonSerializer.SerializeToElement(Convert.ToString(value, CultureInfo.InvariantCulture), Query.QueryValueJson.Default.String),
            _ when target == typeof(GlobalPosition) => value is GlobalPosition p ? p : GlobalPosition.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out GlobalPosition parsed) ? parsed : throw new FormatException("Not a position."),
            _ when target == typeof(string[]) => value is string[] tags ? tags : throw new FormatException("Not a tag array."),
            _ => value,
        };
        return (T)converted;
    }

    /// <summary>Converts a query value to a strongly typed id.</summary>
    public static T Id<T>(object? value)
        where T : struct, IStronglyTypedId<T>
    {
        return value is null ? default : T.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    /// <summary>Converts a query value to a nullable strongly typed id.</summary>
    public static T? NullableId<T>(object? value)
        where T : struct, IStronglyTypedId<T>
    {
        return value is null ? null : T.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    /// <summary>Converts a query value (a number or a name) to an enum.</summary>
    public static T Enum<T>(object? value)
        where T : struct, System.Enum
    {
        return value switch
        {
            null => default,
            string name => System.Enum.Parse<T>(name, ignoreCase: true),
            _ => (T)System.Enum.ToObject(typeof(T), Integer(value)),
        };
    }

    /// <summary>Converts a query value to a nullable enum.</summary>
    public static T? NullableEnum<T>(object? value)
        where T : struct, System.Enum
    {
        return value is null ? null : Enum<T>(value);
    }

    private static long Integer(object value)
    {
        return value switch
        {
            long l => l,
            int i => i,
            decimal d => (long)d,
            _ => long.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
        };
    }
}

