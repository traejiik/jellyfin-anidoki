using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Trackers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace jellyfin_anidoki.Tests.Helpers;

/// <summary>
/// Each tracker has a next allowed time. A call waits the time left until it, never the time since the last call (H4.8),
/// and reserves the next slot one pace later; a back-off only ever pushes it later.
/// </summary>
public class RateLimitTests {
    private IMemoryCache _cache = null!;
    private RecordingDelayer _delayer = null!;

    private sealed class RecordingDelayer : IAsyncDelayer {
        public List<TimeSpan> Waits { get; } = new();

        public Task Delay(TimeSpan timeSpan) {
            Waits.Add(timeSpan);
            return Task.CompletedTask;
        }
    }

    [SetUp]
    public void SetUp() {
        _cache = new MemoryCache(new MemoryCacheOptions());
        _delayer = new RecordingDelayer();
    }

    [TearDown]
    public void TearDown() => _cache.Dispose();

    private Task Call(ApiName provider = ApiName.AniList) => MemoryCacheHelper.CheckRateLimiting(provider, NullLogger.Instance, _cache, _delayer);

    [Test]
    public async Task TheFirstCallDoesNotWait() {
        await Call();

        Assert.That(_delayer.Waits, Is.Empty);
    }

    [Test]
    public async Task ASecondCallWaitsWhatIsLeftOfThePaceAtMost() {
        await Call();
        await Call();

        Assert.That(_delayer.Waits, Has.Count.EqualTo(1));
        Assert.That(_delayer.Waits[0], Is.GreaterThan(TimeSpan.FromSeconds(0.9)).And.LessThanOrEqualTo(MemoryCacheHelper.Pace));
    }

    [Test]
    public async Task EachTrackerIsPacedOnItsOwn() {
        await Call(ApiName.AniList);
        await Call(ApiName.Kitsu);

        Assert.That(_delayer.Waits, Is.Empty);
    }

    [Test]
    public async Task ABackOffIsWaitedOutInFull() {
        // A 429 that asks for 20 seconds: the old limiter waited the time since the last call, about nothing.
        MemoryCacheHelper.SetRateLimitingForProvider(ApiName.AniList, _cache, TimeSpan.FromSeconds(20));

        await Call();

        Assert.That(_delayer.Waits[0], Is.GreaterThan(TimeSpan.FromSeconds(19.5)).And.LessThanOrEqualTo(TimeSpan.FromSeconds(20)));
    }

    [Test]
    public async Task AShorterBackOffNeverShortensALongerOne() {
        MemoryCacheHelper.SetRateLimitingForProvider(ApiName.AniList, _cache, TimeSpan.FromMinutes(2));
        MemoryCacheHelper.SetRateLimitingForProvider(ApiName.AniList, _cache, TimeSpan.FromSeconds(5));

        await Call();

        Assert.That(_delayer.Waits[0], Is.GreaterThan(TimeSpan.FromSeconds(119)));
    }

    [Test]
    public void RateLimitedUntilReportsOnlyAFutureWindow() {
        Assert.That(MemoryCacheHelper.RateLimitedUntil(ApiName.AniList, _cache), Is.Null);

        MemoryCacheHelper.SetRateLimitingForProvider(ApiName.AniList, _cache, TimeSpan.FromMinutes(15));

        Assert.That(MemoryCacheHelper.RateLimitedUntil(ApiName.AniList, _cache),
            Is.EqualTo(DateTimeOffset.UtcNow.AddMinutes(15)).Within(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void RetryAfterInSecondsPushesTheWindow() {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "90");

        MemoryCacheHelper.CheckResponseHeadersForRateLimiting(response, NullLogger.Instance, ApiName.AniList, _cache);

        Assert.That(MemoryCacheHelper.RateLimitedUntil(ApiName.AniList, _cache),
            Is.EqualTo(DateTimeOffset.UtcNow.AddSeconds(90)).Within(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void RetryAfterAsADatePushesTheWindow() {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        DateTimeOffset until = DateTimeOffset.UtcNow.AddMinutes(10);
        response.Headers.TryAddWithoutValidation("Retry-After", until.ToString("R"));

        MemoryCacheHelper.CheckResponseHeadersForRateLimiting(response, NullLogger.Instance, ApiName.AniList, _cache);

        Assert.That(MemoryCacheHelper.RateLimitedUntil(ApiName.AniList, _cache), Is.EqualTo(until).Within(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void AResetGivenInUnixSecondsPushesTheWindow() {
        // AniList sends X-RateLimit-Reset as Unix seconds, which a date parser cannot read.
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(45);
        response.Headers.TryAddWithoutValidation(MemoryCacheHelper.XRateLimitResetHeader, until.ToUnixTimeSeconds().ToString());

        MemoryCacheHelper.CheckResponseHeadersForRateLimiting(response, NullLogger.Instance, ApiName.AniList, _cache);

        Assert.That(MemoryCacheHelper.RateLimitedUntil(ApiName.AniList, _cache), Is.EqualTo(until).Within(TimeSpan.FromSeconds(5)));
    }
}
