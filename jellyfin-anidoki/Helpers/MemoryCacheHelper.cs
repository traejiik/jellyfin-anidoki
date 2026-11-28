using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Trackers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Helpers;

public class MemoryCacheHelper {
    private static string GetNextAllowedKey(ApiName provider) => $"{provider}NextAllowedUtc";
    internal const string XRateLimitRemainingHeader = "X-RateLimit-Remaining";
    internal const string XRateLimitResetHeader = "X-RateLimit-Reset";

    public static string GenerateState(IMemoryCache memoryCache, Guid userId, ApiName provider, string? codeVerifier = null, string? serverId = null, bool returnToAdmin = false) {
        var key = CallbackState.Create(serverId, provider);
        memoryCache.Set(key, new StoredState {
            ApiName = provider,
            UserId = userId,
            CodeVerifier = codeVerifier,
            ReturnToAdmin = returnToAdmin
        }, DateTimeOffset.Now.AddMinutes(LinkExpiryMinutes(Plugin.Instance?.Configuration.authenticationLinkExpireTimeMinutes)));
        return key;
    }

    internal const long DefaultLinkExpiryMinutes = 1440;

    /// <summary>
    /// A configuration that never saved the expiry holds 0; treat that, like any value below 1,
    /// as the 24-hour default the settings page shows, or every sign-in link would expire at once.
    /// </summary>
    internal static long LinkExpiryMinutes(long? configured) =>
        configured is > 0 ? configured.Value : DefaultLinkExpiryMinutes;

    public static StoredState? ConsumeState(IMemoryCache memoryCache, string key) {
        StoredState? storedState = memoryCache.Get<StoredState>(key);
        memoryCache.Remove(key);
        return storedState;
    }

    /// <summary>The gap a tracker keeps between calls: under every tracker's published limit (AniList and Shikimori allow 90 a minute).</summary>
    internal static readonly TimeSpan Pace = TimeSpan.FromSeconds(1);

    private static readonly object WindowLock = new();

    /// <summary>
    /// Waits until the tracker accepts calls again, then reserves the next slot one <see cref="Pace"/> later. It waits the
    /// time left, never the time since the last call (H4.8), so a back-off from a rate limit is waited out in full.
    /// </summary>
    public static async Task CheckRateLimiting(ApiName provider, ILogger logger, IMemoryCache memoryCache, IAsyncDelayer delayer) {
        TimeSpan wait;
        lock (WindowLock) {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset start = NextAllowed(provider, memoryCache) is { } next && next > now ? next : now;
            Store(provider, memoryCache, start + Pace);
            wait = start - now;
        }
        if (wait <= TimeSpan.Zero)
            return;
        logger.LogDebug("({Provider}) Delaying the request to stay under the rate limit", provider);
        long started = Stopwatch.GetTimestamp();
        await delayer.Delay(wait);
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("({Provider}) Waited {ElapsedMs} ms to stay under the rate limit", provider, LogText.ElapsedMs(started));
    }

    /// <summary>Holds the tracker's calls back for <paramref name="backOff"/>. It only ever pushes the next allowed time later.</summary>
    public static void SetRateLimitingForProvider(ApiName provider, IMemoryCache memoryCache, TimeSpan backOff) {
        lock (WindowLock) {
            DateTimeOffset until = DateTimeOffset.UtcNow + backOff;
            if (NextAllowed(provider, memoryCache) is not { } next || next < until)
                Store(provider, memoryCache, until);
        }
    }

    /// <summary>When a rate-limited tracker accepts calls again, or null when it already does.</summary>
    public static DateTimeOffset? RateLimitedUntil(ApiName provider, IMemoryCache memoryCache) =>
        NextAllowed(provider, memoryCache) is { } next && next > DateTimeOffset.UtcNow ? next : null;

    private static DateTimeOffset? NextAllowed(ApiName provider, IMemoryCache memoryCache) =>
        memoryCache.TryGetValue(GetNextAllowedKey(provider), out DateTimeOffset next) ? next : null;

    // Expires with the window, so an idle tracker leaves nothing behind.
    private static void Store(ApiName provider, IMemoryCache memoryCache, DateTimeOffset next) =>
        memoryCache.Set(GetNextAllowedKey(provider), next, next);

    /// <summary>
    /// Check the provided response message for rate limiting headers and add appropriate rate limiting for the provided API provider.
    /// </summary>
    /// <param name="responseMessage">Response message to search the headers of.</param>
    /// <param name="logger">Instance of <see cref="ILogger"/></param>
    /// <param name="provider">Provider to set the rate limiting for.</param>
    /// <param name="memoryCache">Instance of <see cref="IMemoryCache"/></param>
    public static void CheckResponseHeadersForRateLimiting(HttpResponseMessage responseMessage, ILogger logger, ApiName provider, IMemoryCache memoryCache) {
        if (responseMessage.Headers.TryGetValues(XRateLimitRemainingHeader, out IEnumerable<string>? remainingValues)) {
            if (int.TryParse(remainingValues.First(), out int parsedRemaining) && parsedRemaining <= 0) {
                logger.LogWarning("({Provider}) Reached rate limit; waiting 2 minutes before the next request", provider);
                SetRateLimitingForProvider(provider, memoryCache, TimeSpan.FromMinutes(2));
            }
        }

        if (responseMessage.Headers.TryGetValues(XRateLimitResetHeader, out IEnumerable<string>? resetValues)) {
            // AniList sends Unix seconds; others a date.
            string reset = resetValues.First();
            if ((long.TryParse(reset, out long unixSeconds) ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds) : DateTimeOffset.TryParse(reset, out DateTimeOffset parsed) ? parsed : (DateTimeOffset?)null)
                is { } resetTime && resetTime > DateTimeOffset.UtcNow) {
                TimeSpan delay = resetTime - DateTimeOffset.UtcNow;
                logger.LogDebug("({Provider}) Rate limit until {ResetTime}; waiting {DelaySeconds} seconds", provider, resetTime, delay.TotalSeconds);
                SetRateLimitingForProvider(provider, memoryCache, delay);
            }
        }
        if (responseMessage.Headers.RetryAfter is { } retryAfter
            && (retryAfter.Delta ?? (retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : null)) is { } wait && wait > TimeSpan.Zero) {
            logger.LogDebug("({Provider}) Asked to retry after {DelaySeconds} seconds", provider, (int)wait.TotalSeconds);
            SetRateLimitingForProvider(provider, memoryCache, wait);
        }
    }
}
