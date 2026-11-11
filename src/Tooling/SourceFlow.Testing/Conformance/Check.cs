using System.Runtime.CompilerServices;

namespace SourceFlow.Testing.Conformance;

/// <summary>A conformance case failed.</summary>
public sealed class ConformanceException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ConformanceException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner exception.</summary>
    public ConformanceException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Minimal, framework-agnostic assertions for the conformance kits.</summary>
public static class Check
{
    /// <summary>Fails unless <paramref name="condition"/> is true.</summary>
    public static void True(bool condition, string message, [CallerArgumentExpression(nameof(condition))] string expression = "")
    {
        if (!condition)
        {
            throw new ConformanceException($"{message} (expected: {expression})");
        }
    }

    /// <summary>Fails unless the values are equal.</summary>
    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new ConformanceException($"{message}: expected <{expected}>, got <{actual}>.");
        }
    }

    /// <summary>Fails unless the sequences are equal.</summary>
    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        T[] e = expected.ToArray();
        T[] a = actual.ToArray();
        if (!e.SequenceEqual(a))
        {
            throw new ConformanceException($"{message}: expected [{string.Join(", ", e)}], got [{string.Join(", ", a)}].");
        }
    }

    /// <summary>Fails unless <paramref name="value"/> is null.</summary>
    public static void Null(object? value, string message)
    {
        if (value is not null)
        {
            throw new ConformanceException($"{message}: expected null, got <{value}>.");
        }
    }

    /// <summary>Fails unless <paramref name="value"/> is not null.</summary>
    public static T NotNull<T>(T? value, string message)
        where T : class
    {
        return value ?? throw new ConformanceException($"{message}: expected a value, got null.");
    }

    /// <summary>Fails unless the action throws <typeparamref name="TException"/>.</summary>
    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new ConformanceException($"{message}: expected {typeof(TException).Name}, got {other.GetType().Name}: {other.Message}", other);
        }

        throw new ConformanceException($"{message}: expected {typeof(TException).Name}, nothing was thrown.");
    }

    /// <summary>Fails unless the action throws <typeparamref name="TException"/>.</summary>
    public static TException Throws<TException>(Action action, string message)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new ConformanceException($"{message}: expected {typeof(TException).Name}, got {other.GetType().Name}: {other.Message}", other);
        }

        throw new ConformanceException($"{message}: expected {typeof(TException).Name}, nothing was thrown.");
    }
}

/// <summary>One conformance case.</summary>
/// <typeparam name="TFixture">The adapter fixture type.</typeparam>
/// <param name="Id">The stable case id, for example <c>SCK-02</c>.</param>
/// <param name="Name">A short description.</param>
/// <param name="Run">The case body.</param>
/// <param name="RequiresClockControl">True when the case advances the store clock.</param>
public sealed record ConformanceCase<TFixture>(string Id, string Name, Func<TFixture, CancellationToken, Task> Run, bool RequiresClockControl = false)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Id} {Name}";
    }
}
