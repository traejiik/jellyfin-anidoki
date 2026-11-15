using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki_unit_tests.LoggingTests;

/// <summary>One log call as a test sees it: the rendered message and the named values behind it.</summary>
internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties);

/// <summary>A logger factory that keeps every line written through it, for tests that assert what is logged.</summary>
internal sealed class CapturedLogs : ILoggerFactory {
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    /// <summary>The messages written at exactly this level, in order.</summary>
    public string[] At(LogLevel level) => Entries.Where(entry => entry.Level == level).Select(entry => entry.Message).ToArray();

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _entries);

    /// <summary>A typed logger for a class that takes <see cref="ILogger{T}"/> directly.</summary>
    public ILogger<T> For<T>() => new Capture<T>(_entries);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private class Capture(string category, ConcurrentQueue<LogEntry> entries) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Debug is on, so guarded diagnostics run under test too.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) {
            var properties = new Dictionary<string, object?>();
            if (state is IEnumerable<KeyValuePair<string, object?>> values) {
                foreach (var (key, value) in values)
                    properties[key] = value;
            }
            entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception, properties));
        }
    }

    private sealed class Capture<T>(ConcurrentQueue<LogEntry> entries) : Capture(typeof(T).FullName!, entries), ILogger<T>;
}
