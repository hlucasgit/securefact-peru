using Microsoft.Extensions.Logging;

namespace SecureFact.Security.Tests;

/// <summary>Collects every formatted log message (and exception text) so tests can prove nothing sensitive is logged.</summary>
public sealed class LogSink : ILoggerProvider
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Snapshot()
    {
        lock (_entries)
        {
            return [.. _entries];
        }
    }

    public ILogger CreateLogger(string categoryName) => new SinkLogger(this);

    public void Dispose()
    {
    }

    private void Add(string entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    private sealed class SinkLogger(LogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Add($"{formatter(state, exception)} {exception}");
    }
}
