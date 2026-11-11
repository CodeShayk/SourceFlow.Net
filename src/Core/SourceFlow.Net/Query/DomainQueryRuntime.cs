namespace SourceFlow.Query;

/// <summary>A registered domain query (doc 14 Q-B4): its contract and how to run it.</summary>
public abstract class DomainQueryDescriptor
{
    private protected DomainQueryDescriptor()
    {
    }

    /// <summary>The query name.</summary>
    public abstract string Name { get; }

    /// <summary>The contract version.</summary>
    public abstract int Version { get; }

    /// <summary>The query text.</summary>
    public abstract string Text { get; }

    /// <summary>The syntax of <see cref="Text"/>.</summary>
    public abstract QueryLanguage Language { get; }

    /// <summary>The parameters.</summary>
    public abstract IReadOnlyList<DomainQueryParameter> Parameters { get; }

    /// <summary>The result record's columns.</summary>
    public abstract IReadOnlyList<string> ResultColumns { get; }

    /// <summary>The query type.</summary>
    public abstract Type QueryType { get; }

    /// <summary>The result type.</summary>
    public abstract Type ResultType { get; }

    internal abstract ValueTask<object> ExecuteAsync(IQueryService service, object query, QueryOptions? options, CancellationToken cancellationToken);
}

internal sealed class DomainQueryDescriptor<TQuery, TResult> : DomainQueryDescriptor
    where TQuery : class, IDomainQueryRuntime<TQuery, TResult>
    where TResult : class
{
    public override string Name => TQuery.QueryName;

    public override int Version => TQuery.QueryVersion;

    public override string Text => TQuery.QueryText;

    public override QueryLanguage Language => TQuery.IsSql ? QueryLanguage.Sql : QueryLanguage.Sfql;

    public override IReadOnlyList<DomainQueryParameter> Parameters => TQuery.QueryParameters;

    public override IReadOnlyList<string> ResultColumns => TQuery.ResultColumns;

    public override Type QueryType => typeof(TQuery);

    public override Type ResultType => typeof(TResult);

    internal override async ValueTask<object> ExecuteAsync(IQueryService service, object query, QueryOptions? options, CancellationToken cancellationToken)
    {
        options = (options ?? new QueryOptions()) with { Language = Language };
        var results = new List<TResult>();
        await foreach (QueryRow row in service.QueryAsync(Text, ((TQuery)query).ToParameters(), options, cancellationToken).ConfigureAwait(false))
        {
            results.Add(TQuery.Materialize(row));
        }

        return results;
    }
}

/// <summary>A domain query that doesn't bind against the catalog, or whose columns don't match its result (SF0018).</summary>
public sealed class DomainQueryException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public DomainQueryException(string message)
        : base(message)
    {
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class QueryValueJson : System.Text.Json.Serialization.JsonSerializerContext;
