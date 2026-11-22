using System;
using System.Collections.Concurrent;
using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace jellyfin_anidoki.Logging;

/// <summary>
/// Copies AniDōki's log lines to their own daily file in Jellyfin's log folder (anidoki_YYYYMMDD.log), so an admin can
/// download them from Dashboard → Logs. Jellyfin logs through Serilog's static logger and ignores logging providers a
/// plugin registers, so the copy is made by wrapping that logger once at plugin start: every event still reaches
/// Jellyfin's logger unchanged, under its own levels and overrides, and AniDōki's events also reach the file.
/// </summary>
public static class AniDokiLogFile {
    public const string FilePrefix = "anidoki_";
    private const string Category = "jellyfin_anidoki";
    private static readonly object Gate = new();

    /// <summary>The file's lowest level: Debug, or Information while the admin has debug lines switched off.</summary>
    private static readonly LoggingLevelSwitch FileLevel = new(LogEventLevel.Debug);

    /// <summary>The folder the file is written to, or null when it could not be set up (Jellyfin's log still has every line).</summary>
    public static string? Folder { get; private set; }

    /// <summary>Applies the admin's choice; takes effect for the next line.</summary>
    public static void IncludeDebugLines(bool include) =>
        FileLevel.MinimumLevel = include ? LogEventLevel.Debug : LogEventLevel.Information;

    /// <summary>
    /// Wraps Jellyfin's logger, once. Loggers created afterwards (every AniDōki logger) write through the wrapper.
    /// Does nothing when Jellyfin's log folder is unknown.
    /// </summary>
    public static void Attach() {
        lock (Gate) {
            if (Folder != null)
                return;
            string? folder = Environment.GetEnvironmentVariable("JELLYFIN_LOG_DIR");
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;
            Log.Logger = Wrap(Log.Logger, folder, FileLevel);
            Folder = folder;
        }
    }

    /// <summary>
    /// A logger that hands every event to <paramref name="jellyfin"/> and also writes AniDōki's events at
    /// <paramref name="fileLevel"/> and above to the file.
    /// </summary>
    internal static Logger Wrap(ILogger jellyfin, string folder, LoggingLevelSwitch fileLevel) =>
        new LoggerConfiguration()
            // Never stricter than Jellyfin's default level, and never above Debug, so a Debug override in Jellyfin's
            // logging.json still works; AniDōki's Debug always passes, for the file.
            .MinimumLevel.Is(Min(LowestEnabled(jellyfin), LogEventLevel.Debug))
            .MinimumLevel.Override(Category, LogEventLevel.Debug)
            .WriteTo.Sink(new ToJellyfin(jellyfin))
            .WriteTo.Logger(file => file
                .MinimumLevel.ControlledBy(fileLevel)
                .Filter.ByIncludingOnly(IsAniDoki)
                .Enrich.With<OperationContext>()
                .WriteTo.File(Path.Combine(folder, FilePrefix + ".log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 20_000_000,
                    rollOnFileSizeLimit: true,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {OperationContext}{SourceContext}: {Message}{NewLine}{Exception}"))
            .CreateLogger();

    private static LogEventLevel Min(LogEventLevel first, LogEventLevel second) => first < second ? first : second;

    private static LogEventLevel LowestEnabled(ILogger logger) {
        foreach (LogEventLevel level in Enum.GetValues<LogEventLevel>()) {
            if (logger.IsEnabled(level))
                return level;
        }
        return LogEventLevel.Fatal;
    }

    private static string? Source(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value) && value is ScalarValue { Value: string name } ? name : null;

    private static bool IsAniDoki(LogEvent logEvent) =>
        Source(logEvent) is { } name && (name == Category || name.StartsWith(Category + ".", StringComparison.Ordinal));

    /// <summary>
    /// Hands each event to Jellyfin's logger under the event's own source, so Jellyfin's per-source level overrides
    /// (Microsoft and System at Warning) still decide what reaches its log. Disposing it flushes Jellyfin's logger, as
    /// Jellyfin's shutdown now closes the wrapper.
    /// </summary>
    private sealed class ToJellyfin(ILogger jellyfin) : ILogEventSink, IDisposable {
        private readonly ConcurrentDictionary<string, ILogger> _bySource = new();

        public void Emit(LogEvent logEvent) {
            string? source = Source(logEvent);
            ILogger target = source == null ? jellyfin : _bySource.GetOrAdd(source, name => jellyfin.ForContext(Constants.SourceContextPropertyName, name));
            target.Write(logEvent);
        }

        public void Dispose() => (jellyfin as IDisposable)?.Dispose();
    }

    /// <summary>Puts "[Playback 2e0d… user d468…] " before a line written while an operation runs, from the runner's scope.</summary>
    private sealed class OperationContext : ILogEventEnricher {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) {
            string? id = Text(logEvent, "OperationId");
            string text = id == null ? "" : $"[{Text(logEvent, "OperationKind")} {id} user {Text(logEvent, "UserId")}] ";
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("OperationContext", text));
        }

        private static string? Text(LogEvent logEvent, string name) =>
            logEvent.Properties.TryGetValue(name, out var value) ? value is ScalarValue scalar ? scalar.Value?.ToString() : value.ToString() : null;
    }
}
