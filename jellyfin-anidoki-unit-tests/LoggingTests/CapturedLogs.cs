using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki_unit_tests.LoggingTests;

/// <summary>One log call as a test sees it: the rendered message, the named values behind it, and the scope it was written in.</summary>
internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception,
    IReadOnlyDictionary<string, object?> Properties, IReadOnlyDictionary<string, object?> Scope);

/// <summary>A logger factory that keeps every line written through it, for tests that assert what is logged.</summary>
internal sealed class CapturedLogs : ILoggerFactory {
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    // One scope chain for the whole factory: a scope begun on one category's logger applies to every other's, as on the server.
    private readonly AsyncLocal<ScopeFrame?> _scope = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    /// <summary>The messages written at exactly this level, in order.</summary>
    public string[] At(LogLevel level) => Entries.Where(entry => entry.Level == level).Select(entry => entry.Message).ToArray();

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _entries, _scope);

    /// <summary>A typed logger for a class that takes <see cref="ILogger{T}"/> directly.</summary>
    public ILogger<T> For<T>() => new Capture<T>(_entries, _scope);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed record ScopeFrame(ScopeFrame? Parent, object State);

    private class Capture(string category, ConcurrentQueue<LogEntry> entries, AsyncLocal<ScopeFrame?> scope) : ILogger {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull {
            ScopeFrame? parent = scope.Value;
            scope.Value = new ScopeFrame(parent, state);
            return new Pop(() => scope.Value = parent);
        }

        // Debug is on, so guarded diagnostics run under test too.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) {
            entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception, Values(state), CurrentScope()));
        }

        private Dictionary<string, object?> CurrentScope() {
            // Outermost first, so an inner scope's value wins.
            var frames = new Stack<ScopeFrame>();
            for (ScopeFrame? frame = scope.Value; frame != null; frame = frame.Parent)
                frames.Push(frame);
            var values = new Dictionary<string, object?>();
            foreach (ScopeFrame frame in frames) {
                foreach (var (key, value) in Values(frame.State))
                    values[key] = value;
            }
            return values;
        }

        private static Dictionary<string, object?> Values(object? state) {
            var values = new Dictionary<string, object?>();
            // Nullability is not a runtime type, so this also matches the Dictionary<string, object> the runner passes.
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs) {
                foreach (var (key, value) in pairs)
                    values[key] = value;
            }
            return values;
        }
    }

    private sealed class Pop(Action pop) : IDisposable {
        public void Dispose() => pop();
    }

    private sealed class Capture<T>(ConcurrentQueue<LogEntry> entries, AsyncLocal<ScopeFrame?> scope)
        : Capture(typeof(T).FullName!, entries, scope), ILogger<T>;
}
