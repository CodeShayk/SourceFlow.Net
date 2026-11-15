namespace SourceFlow;

/// <summary>
/// Declares a named, versioned domain query (doc 14 Q-B4): the stable query contract a service offers other services and
/// UIs. The type is a <c>partial record</c> implementing <see cref="IDomainQuery{TResult}"/>; its members are the query
/// parameters (camelCase, matching <c>@name</c> in the text) and a <c>const string Sfql</c> (or <c>Sql</c>) holds the text.
/// The generator implements it, registers it with the module, and analyzer SF0018 checks its shape at build time; the
/// host binds it against the catalog at startup.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DomainQueryAttribute : Attribute
{
    /// <summary>Declares the query.</summary>
    /// <param name="name">The query name, for example <c>accounts.overdrawn-accounts</c>.</param>
    /// <param name="version">The contract version (default 1).</param>
    public DomainQueryAttribute(string name, int version = 1)
    {
        Name = name;
        Version = version;
    }

    /// <summary>The query name.</summary>
    public string Name { get; }

    /// <summary>The contract version.</summary>
    public int Version { get; }

    /// <summary>
    /// The authorization policy a caller of the query API must satisfy to list or run this query (doc 14 §9.1), on top of
    /// the policy of the endpoint group. Null: the group's policy alone.
    /// </summary>
    public string? Policy { get; set; }
}

/// <summary>A named domain query returning rows of <typeparamref name="TResult"/> (doc 14 Q-B4).</summary>
/// <typeparam name="TResult">The result record; its constructor parameters match the query's output columns.</typeparam>
public interface IDomainQuery<TResult>
    where TResult : class;

/// <summary>A query parameter of a domain query.</summary>
/// <param name="Name">The parameter name in the query text (without <c>@</c>).</param>
/// <param name="Type">The CLR type of the member.</param>
public sealed record DomainQueryParameter(string Name, Type Type);

/// <summary>The generated runtime of a domain query (implemented by the source generator).</summary>
/// <typeparam name="TSelf">The query type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
public interface IDomainQueryRuntime<TSelf, TResult> : IDomainQuery<TResult>
    where TSelf : class, IDomainQueryRuntime<TSelf, TResult>
    where TResult : class
{
    /// <summary>The query name.</summary>
    static abstract string QueryName { get; }

    /// <summary>The contract version.</summary>
    static abstract int QueryVersion { get; }

    /// <summary>The authorization policy of the query API endpoint (<see cref="DomainQueryAttribute.Policy"/>); null for none.</summary>
    static virtual string? QueryPolicy => null;

    /// <summary>The query text.</summary>
    static abstract string QueryText { get; }

    /// <summary>True when <see cref="QueryText"/> is in the SQL syntax.</summary>
    static abstract bool IsSql { get; }

    /// <summary>The parameters.</summary>
    static abstract IReadOnlyList<DomainQueryParameter> QueryParameters { get; }

    /// <summary>The result record's constructor parameters, in order (matched to output columns case-insensitively).</summary>
    static abstract IReadOnlyList<string> ResultColumns { get; }

    /// <summary>This instance's parameter values by name.</summary>
    IReadOnlyDictionary<string, object?> ToParameters();

    /// <summary>Builds a result from a row.</summary>
    static abstract TResult Materialize(IQueryRow row);
}
