using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using jellyfin_anidoki.Logging;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace jellyfin_anidoki_unit_tests.Logging;

public class AniDokiLogFileTests {
    private sealed class Collected : ILogEventSink, IDisposable {
        public List<LogEvent> Events { get; } = new();
        public bool Disposed { get; private set; }
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        public void Dispose() => Disposed = true;
    }

    private string _folder = null!;
    private Collected _jellyfin = null!;

    [SetUp]
    public void SetUp() {
        _folder = Path.Combine(Path.GetTempPath(), "anidoki-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _jellyfin = new Collected();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_folder, recursive: true);

    /// <summary>Jellyfin's default logging.json: Information, with Microsoft and System at Warning.</summary>
    private Logger Jellyfin() => new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .WriteTo.Sink(_jellyfin)
        .CreateLogger();

    private string[] Written(Logger wrapper, Action<Microsoft.Extensions.Logging.ILoggerFactory> log) {
        using (var factory = new SerilogLoggerFactory(wrapper, dispose: true))
            log(factory);
        // The file sink creates the file on its first line, so no file means nothing was written.
        return Directory.GetFiles(_folder, AniDokiLogFile.FilePrefix + "*.log").SelectMany(File.ReadAllLines).ToArray();
    }

    private string[] Written(Action<Microsoft.Extensions.Logging.ILoggerFactory> log, LogEventLevel fileLevel = LogEventLevel.Debug) =>
        Written(AniDokiLogFile.Wrap(Jellyfin(), _folder, new LoggingLevelSwitch(fileLevel)), log);

    private IEnumerable<string> JellyfinLines() => _jellyfin.Events.Select(e => $"{e.Level} {e.RenderMessage()}");

    [Test]
    public void AniDokiDebugLinesGoToTheFileOnlyAndInformationToBoth() {
        var lines = Written(factory => {
            var logger = factory.CreateLogger("jellyfin_anidoki.SyncPipeline");
            logger.LogDebug("Resolved {Title}", "Trigun");
            logger.LogInformation("Synced {Title}", "Trigun");
        });

        Assert.That(lines, Has.Length.EqualTo(2));
        Assert.That(lines[0], Does.Contain("[DBG] jellyfin_anidoki.SyncPipeline: Resolved \"Trigun\""));
        Assert.That(lines[1], Does.Contain("[INF] jellyfin_anidoki.SyncPipeline: Synced \"Trigun\""));
        Assert.That(JellyfinLines(), Is.EqualTo(new[] { "Information Synced \"Trigun\"" }));
    }

    [Test]
    public void OtherSourcesReachJellyfinUnderItsOwnLevelsAndNeverTheFile() {
        var lines = Written(factory => {
            factory.CreateLogger("Emby.Server.Implementations.Library").LogInformation("Scanned");
            factory.CreateLogger("Emby.Server.Implementations.Library").LogDebug("Hidden");
            factory.CreateLogger("Microsoft.AspNetCore.Hosting").LogInformation("Request");
            factory.CreateLogger("Microsoft.AspNetCore.Hosting").LogWarning("Slow");
            factory.CreateLogger("jellyfin_anidoki_other").LogInformation("Not ours");
        });

        Assert.That(lines, Is.Empty);
        Assert.That(JellyfinLines(), Is.EqualTo(new[] { "Information Scanned", "Warning Slow", "Information Not ours" }));
    }

    [Test]
    public void WithDebugSwitchedOffTheFileStartsAtInformation() {
        var lines = Written(factory => {
            var logger = factory.CreateLogger("jellyfin_anidoki.Sync");
            logger.LogDebug("Step");
            logger.LogInformation("Done");
        }, LogEventLevel.Information);

        Assert.That(lines, Has.Length.EqualTo(1));
        Assert.That(lines[0], Does.Contain("[INF] jellyfin_anidoki.Sync: Done"));
    }

    [Test]
    public void TheSwitchAppliesToTheNextLine() {
        var level = new LoggingLevelSwitch(LogEventLevel.Debug);
        var lines = Written(AniDokiLogFile.Wrap(Jellyfin(), _folder, level), factory => {
            var logger = factory.CreateLogger("jellyfin_anidoki.Sync");
            logger.LogDebug("Before");
            level.MinimumLevel = LogEventLevel.Information;
            logger.LogDebug("After");
        });

        Assert.That(lines.Single(), Does.Contain("Before"));
    }

    [Test]
    public void ALineWrittenDuringAnOperationNamesItsKindIdAndUser() {
        var operation = Guid.Parse("2e0da6dd-7589-4feb-8e52-a556c3ee143f");
        var user = Guid.Parse("d468bb03-9cfc-41f5-a7d4-67e541bcb9ee");
        var lines = Written(factory => {
            var runner = factory.CreateLogger("jellyfin_anidoki.Operations.OperationRunner");
            using (runner.BeginScope(new Dictionary<string, object> { ["OperationId"] = operation, ["OperationKind"] = "Playback", ["UserId"] = user }))
                factory.CreateLogger("jellyfin_anidoki.SyncPipeline").LogInformation("Confirmed");
            factory.CreateLogger("jellyfin_anidoki.SyncPipeline").LogInformation("Outside");
        });

        Assert.That(lines[0], Does.Contain($"[INF] [Playback {operation} user {user}] jellyfin_anidoki.SyncPipeline: Confirmed"));
        Assert.That(lines[1], Does.Contain("[INF] jellyfin_anidoki.SyncPipeline: Outside"));
    }

    [Test]
    public void ClosingTheWrapperClosesJellyfinsLogger() {
        var wrapper = AniDokiLogFile.Wrap(Jellyfin(), _folder, new LoggingLevelSwitch());

        wrapper.Dispose();

        Assert.That(_jellyfin.Disposed, Is.True, "Jellyfin closes only the wrapper at shutdown; its own sinks must still flush");
    }
}
