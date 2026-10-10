#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
namespace jellyfin_anidoki.Notifications.Discord;

public sealed class DiscordNotificationDispatcher(DiscordSettingsService settings, DiscordWebhookSender sender, DiscordPosterProvider posters,
    IUserManager users, ILibraryManager library, IServerApplicationHost host, TimeProvider clock) : BackgroundService
{
    private sealed class Job(Guid user, long revision, SyncOperationSummary? summary, string destination, DateTimeOffset now, bool test)
    {
        public Guid User = user; public long Revision = revision; public SyncOperationSummary? Summary = summary; public string Destination = destination;
        public DateTimeOffset Deadline = now.AddMinutes(2), Due = now; public bool Sending, Test = test; public int Retries;
        public TaskCompletionSource<DiscordDeliveryResult>? Completion = test ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
    }
    private readonly object _lock = new(); private readonly List<Job> _jobs = [];
    private readonly Dictionary<(Guid User, Guid Operation), DateTimeOffset> _seen = [];
    private readonly Dictionary<string, DateTimeOffset> _destinationBackoff = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastTest = [];
    private DateTimeOffset _globalBackoff;
    public bool Enqueue(SyncOperationSummary summary)
    {
        // Delivery is best effort and must never change the tracker operation's result.
        try
        {
            if (!summary.Outcomes.Any(o => o.Kind == OutcomeKind.Confirmed && o.MeaningfulChange)) return false;
            var own = settings.Read(summary.UserId); if (!own.Enabled) return false;
            return Add(summary.UserId, own.Revision, summary, false) != null;
        }
        catch (Exception) { return false; }
    }
    public Task<DiscordDeliveryResult>? Test(Guid user, long revision, SyncOperationSummary? summary)
    {
        lock (_lock) { var now = clock.GetUtcNow(); if (_lastTest.TryGetValue(user, out var last) && now - last < TimeSpan.FromMinutes(1)) return null; _lastTest[user] = now; }
        return Add(user, revision, summary, true)?.Completion?.Task;
    }
    private Job? Add(Guid user, long revision, SyncOperationSummary? summary, bool test)
    {
        var destination = settings.Destination(user, revision, test); if (destination == null) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination.Value.Webhook)));
        lock (_lock)
        {
            var now = clock.GetUtcNow(); foreach (var old in _seen.Where(p => now - p.Value >= TimeSpan.FromMinutes(10)).Select(p => p.Key).ToArray()) _seen.Remove(old);
            if (_jobs.Count >= 100 || _jobs.Count(j => j.User == user) >= 10 || !test && summary != null && _seen.ContainsKey((user, summary.OperationId))) return null;
            if (!test && summary != null) { if (_seen.Count >= 2048) return null; _seen[(user, summary.OperationId)] = now; }
            var job = new Job(user, revision, summary == null ? null : summary with { Outcomes = summary.Outcomes.ToArray() }, key, now, test); _jobs.Add(job); return job;
        }
    }
    protected override async Task ExecuteAsync(CancellationToken cancellation)
    {
        var running = new List<Task>();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                running.RemoveAll(t => t.IsCompleted);
                var claimed = new List<Job>();
                lock (_lock)
                {
                    var now = clock.GetUtcNow();
                    foreach (var expired in _jobs.Where(j => !j.Sending && j.Deadline <= now).ToArray()) { _jobs.Remove(expired); expired.Completion?.TrySetResult(new(DiscordDeliveryKind.Cancelled, TimeSpan.Zero)); }
                    foreach (var old in _destinationBackoff.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _destinationBackoff.Remove(old);
                    if (_globalBackoff <= now) foreach (var job in _jobs.Where(j => !j.Sending && j.Due <= now).ToArray())
                    {
                        if (running.Count + claimed.Count >= 2) break;
                        if (_jobs.Any(j => j.Sending && j.Destination == job.Destination) || _destinationBackoff.TryGetValue(job.Destination, out var until) && until > now) continue;
                        job.Sending = true; claimed.Add(job);
                    }
                }
                // Start image and HTTP work after releasing the queue lock.
                foreach (var job in claimed) running.Add(Send(job, cancellation));
                await Task.Delay(100, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { await Task.WhenAll(running); lock (_lock) { foreach (var job in _jobs) job.Completion?.TrySetResult(new(DiscordDeliveryKind.Cancelled, TimeSpan.Zero)); _jobs.Clear(); } }
    }
    private async Task Send(Job job, CancellationToken cancellation)
    {
        DiscordDeliveryResult result = new(DiscordDeliveryKind.Cancelled, TimeSpan.Zero);
        try
        {
            var destination = settings.Destination(job.User, job.Revision, job.Test);
            var user = users.GetUserById(job.User); var summary = job.Summary;
            if (destination != null && user != null && job.Deadline > clock.GetUtcNow())
            {
                var source = summary == null ? null : library.GetItemById(summary.SourceItemId);
                var show = summary == null ? null : library.GetItemById(summary.ShowItemId);
                if (summary == null || source != null && show != null && source.IsVisible(user, false) && show.IsVisible(user, false))
                {
                    var poster = show == null ? null : await posters.Read(show, cancellation);
                    var payload = summary == null ? DiscordEmbedBuilder.GenericTest(host.FriendlyName, clock.GetUtcNow()) :
                        DiscordEmbedBuilder.Build(summary, destination.Value.Address, host.SystemId, poster != null, job.Test, host.FriendlyName, clock.GetUtcNow());
                    // Re-check destination revision after image processing, before transmission.
                    if (settings.Destination(job.User, job.Revision, job.Test) == destination && job.Deadline > clock.GetUtcNow())
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        result = await sender.Send(destination.Value.Webhook, payload, poster, timeout.Token);
                    }
                }
            }
        }
        catch (Exception) { result = new(DiscordDeliveryKind.Unconfirmed, TimeSpan.Zero); }
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            if (result.Kind == DiscordDeliveryKind.RateLimited) { if (result.Global) _globalBackoff = now + result.Delay; else _destinationBackoff[job.Destination] = now + result.Delay; }
            if (result.Kind == DiscordDeliveryKind.Rejected) settings.Block(job.User, job.Revision);
            if (result.Kind == DiscordDeliveryKind.Delivered) settings.Unblock(job.User, job.Revision);
            if (result.Kind == DiscordDeliveryKind.RateLimited && job.Retries < 2 && now + result.Delay < job.Deadline && !cancellation.IsCancellationRequested)
            {
                job.Retries++; job.Due = now + result.Delay; job.Sending = false;
                if (result.Global) _globalBackoff = job.Due; else _destinationBackoff[job.Destination] = job.Due;
                return;
            }
            _jobs.Remove(job); settings.Health(job.User, job.Revision, result.Message); job.Completion?.TrySetResult(result);
        }
    }
}
