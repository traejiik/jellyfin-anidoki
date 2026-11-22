using System;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Operations;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Playback;

/// <summary>
/// D6: at startup and every minute, runs the retries that are due. Each is a fresh <see cref="OperationKind.Retry"/> for one
/// tracker, queued behind the user's other updates, that re-plans from the tracker's current state. Registered after
/// <see cref="OperationRunner"/>.
/// </summary>
public sealed class RetryWorker(RetryQueue retries, OperationRunner runner, TriggerReplay replay, ILibraryManager libraryManager,
    ILogger<RetryWorker> logger) : IHostedService, IDisposable {
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private Timer? _timer;
    private int _running;

    /// <summary>The season an item belongs to, which a newer completion's retry replaces; a movie, or an item that is gone, is its own.</summary>
    public static string SeriesKey(ILibraryManager libraryManager, Guid itemId) => libraryManager.GetItemById(itemId) switch {
        Episode { SeasonId: var season } when season != Guid.Empty => season.ToString("N"),
        Episode { SeriesId: var series } when series != Guid.Empty => series.ToString("N"),
        _ => itemId.ToString("N"),
    };

    public Task StartAsync(CancellationToken cancellationToken) {
        _timer = new Timer(_ => RunDue(), null, TimeSpan.Zero, Interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary>Starts every due retry. A tick that finds the last one still going does nothing.</summary>
    /// <returns>How many retries were started.</returns>
    internal int RunDue() {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return 0;
        try {
            int started = 0;
            foreach (RetryRow row in retries.Due()) {
                if (Start(row))
                    started++;
            }
            return started;
        } catch (Exception error) when (error is InvalidOperationException or SqliteException) {
            logger.LogWarning("Could not read the tracker retries that are due: {ErrorMessage}", error.Message);
            return 0;
        } finally {
            Volatile.Write(ref _running, 0);
        }
    }

    private bool Start(RetryRow row) {
        BaseItem? item = libraryManager.GetItemById(row.SourceItem);
        if (item == null || SyncHelper.MediaShouldBeIgnored(item)) {
            retries.Remove(row.Id);
            logger.LogDebug("({Provider}) Dropped a retry, because its item is gone or ignored", row.Provider);
            return false;
        }
        Guid operationId = Guid.NewGuid();
        // Before enqueuing, so the next tick does not start it twice.
        retries.Lease(row.Id, operationId);
        var subject = new RetrySubject(row.SourceItem, row.Trigger, row.Attempt, row.Provider);
        var handle = runner.Enqueue(row.UserId, OperationKind.Retry, OperationSources.Describe(item),
            operation => replay.Run(item, row.UserId, row.Trigger, row.Provider, operation), operationId, retry: subject);
        if (handle == null) {
            logger.LogWarning("({Provider}) Retry for {ItemName} was not started because AniDoki storage is unavailable", row.Provider, item.Name);
            return false;
        }
        logger.LogDebug("({Provider}) Retrying {ItemName}, attempt {Attempt}", row.Provider, item.Name, row.Attempt);
        return true;
    }
}
