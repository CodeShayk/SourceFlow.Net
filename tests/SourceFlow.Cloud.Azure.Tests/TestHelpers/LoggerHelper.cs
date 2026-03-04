using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace SourceFlow.Cloud.Azure.Tests.TestHelpers;

/// <summary>
/// Helper utilities for creating loggers in tests.
/// </summary>
public static class LoggerHelper
{
    /// <summary>
    /// Creates a logger that outputs to xUnit test output.
    /// </summary>
    public static ILogger<T> CreateLogger<T>(ITestOutputHelper output)
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddXUnit(output);
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        return loggerFactory.CreateLogger<T>();
    }

    /// <summary>
    /// Creates a logger factory that outputs to xUnit test output.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(ITestOutputHelper output)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.AddXUnit(output);
            builder.SetMinimumLevel(LogLevel.Debug);
        });
    }
}

/// <summary>
/// Extension methods for adding xUnit logging to ILoggingBuilder.
/// </summary>
public static class XUnitLoggingExtensions
{
    /// <summary>
    /// Adds xUnit test output logging to the logging builder.
    /// </summary>
    public static ILoggingBuilder AddXUnit(this ILoggingBuilder builder, ITestOutputHelper output)
    {
        builder.AddProvider(new XUnitLoggerProvider(output));
        return builder;
    }
}

/// <summary>
/// Logger provider that outputs to xUnit test output.
/// </summary>
internal class XUnitLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _output;

    public XUnitLoggerProvider(ITestOutputHelper output)
    {
        _output = output;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new XUnitLogger(_output, categoryName);
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Logger that outputs to xUnit test output.
/// </summary>
internal class XUnitLogger : ILogger
{
    private readonly ITestOutputHelper _output;
    private readonly string _categoryName;

    public XUnitLogger(ITestOutputHelper output, string categoryName)
    {
        _output = output;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        try
        {
            var message = formatter(state, exception);
            var logMessage = $"[{logLevel}] {_categoryName}: {message}";
            
            if (exception != null)
            {
                logMessage += Environment.NewLine + exception;
            }

            _output.WriteLine(logMessage);
        }
        catch
        {
            // Ignore errors writing to test output
        }
    }
}
