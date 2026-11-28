using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Auth;

public class AuthApiCall {
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerApplicationHost _serverApplicationHost;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AuthApiCall> _logger;
    private readonly IMemoryCache _memoryCache;
    private readonly IAsyncDelayer _delayer;
    public UserConfig? UserConfig { get; set; }
    private static readonly int Default429Timeout = 20;
    private static readonly int TimeoutIncrementMultiplier = 2;

    public AuthApiCall(IHttpClientFactory httpClientFactory,
        IServerApplicationHost serverApplicationHost,
        IHttpContextAccessor httpContextAccessor,
        ILoggerFactory loggerFactory,
        IMemoryCache memoryCache,
        IAsyncDelayer delayer,
        UserConfig? userConfig) {
        _httpClientFactory = httpClientFactory;
        _serverApplicationHost = serverApplicationHost;
        _httpContextAccessor = httpContextAccessor;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AuthApiCall>();
        _memoryCache = memoryCache;
        _delayer = delayer;
        UserConfig = userConfig;
    }

    /// <summary>
    /// Make an authenticated API call.
    /// </summary>
    /// <param name="callType">The type of call to make.</param>
    /// <param name="url">The URL that you want to make the request to.</param>
    /// <param name="formUrlEncodedContent">The form data to be posted.</param>
    /// <returns>API call response.</returns>
    /// <exception cref="NullReferenceException">Authentication details not found.</exception>
    /// <exception cref="Exception">Non-200 response.</exception>
    /// <exception cref="AuthenticationException">Could not authenticate with the API.</exception>
    public async Task<HttpResponseMessage?> AuthenticatedApiCall(ApiName provider, CallType callType, string url, FormUrlEncodedContent? formUrlEncodedContent = null, StringContent? stringContent = null, Dictionary<string, string>? requestHeaders = null) {
        int attempts = 0;
        int timeoutSeconds = Default429Timeout;
        TrackerFailureKind lastFailure = TrackerFailureKind.Unauthorized;
        // A client built without a user has no linked account, like a user who never linked this tracker.
        UserConfig? user = UserConfig;
        UserApiAuth? auth = user?.UserApiAuth?.FirstOrDefault(item => item.Name == provider);
        if (user == null || auth == null) {
            _logger.LogWarning("({Provider}) No linked account for user {UserId}; link the account on the AniDoki page", provider, user?.UserId);
            TrackerCallScope.Report(TrackerFailureKind.Unauthorized);
            return null;
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        // added once; the client is reused for every retry below
        if (requestHeaders != null) {
            foreach (KeyValuePair<string, string> requestHeader in requestHeaders) {
                client.DefaultRequestHeaders.Add(requestHeader.Key, requestHeader.Value);
            }
        }

        while (attempts < 3) {
            await MemoryCacheHelper.CheckRateLimiting(provider, _logger, _memoryCache, _delayer);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            HttpResponseMessage responseMessage;
            try {
                switch (callType) {
                    case CallType.GET:
                        responseMessage = await client.GetAsync(url);
                        break;
                    case CallType.POST:
                        responseMessage = await client.PostAsync(url, formUrlEncodedContent != null ? formUrlEncodedContent : stringContent);
                        break;
                    case CallType.PATCH:
                        responseMessage = await client.PatchAsync(url, formUrlEncodedContent != null ? formUrlEncodedContent : stringContent);
                        break;
                    case CallType.PUT:
                        responseMessage = await client.PutAsync(url, formUrlEncodedContent);
                        break;
                    case CallType.DELETE:
                        responseMessage = await client.DeleteAsync(url);
                        break;
                    default:
                        responseMessage = await client.GetAsync(url);
                        break;
                }
            } catch (Exception e) {
                // no response arrived, so the call can't be treated as a success; a write may still have landed
                _logger.LogWarning("({Provider}) {CallType} {Url} failed before a response arrived: {ErrorMessage}", provider, callType, url, e.Message);
                TrackerCallScope.Report(TrackerFailureKind.Transient, responseReceived: false);
                return null;
            }

            MemoryCacheHelper.CheckResponseHeadersForRateLimiting(responseMessage, _logger, provider, _memoryCache);

            if (responseMessage.IsSuccessStatusCode) {
                return responseMessage;
            } else {
                switch (await EffectiveStatus(provider, responseMessage)) {
                    case HttpStatusCode.Unauthorized:
                        lastFailure = TrackerFailureKind.Unauthorized;
                        // token has probably expired; try refreshing it
                        UserApiAuth? newAuth;
                        try {
                            newAuth = await new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory, _memoryCache, _delayer).GetToken(user.UserId, refreshToken: auth.RefreshToken);
                        } catch (Exception e) {
                            _logger.LogWarning("({Provider}) Could not refresh the sign-in of user {UserId}; link the account again on the AniDoki page: {ErrorMessage}", provider, user.UserId, e.Message);
                            TrackerCallScope.Report(TrackerFailureKind.Unauthorized);
                            return null;
                        }

                        // No token (Shikimori without an app name) fails the call like a refresh that threw.
                        if (newAuth == null) {
                            _logger.LogWarning("({Provider}) Could not refresh the sign-in of user {UserId}; link the account again on the AniDoki page", provider, user.UserId);
                            TrackerCallScope.Report(TrackerFailureKind.Unauthorized);
                            return null;
                        }

                        // and then make the call again, using the new auth details
                        auth = newAuth;
                        attempts++;
                        break;
                    case HttpStatusCode.TooManyRequests:
                        lastFailure = TrackerFailureKind.RateLimited;
                        // The tracker's Retry-After, already applied from the headers above, wins when it is longer.
                        int waitSeconds = Math.Max(timeoutSeconds, (int)Math.Ceiling(((MemoryCacheHelper.RateLimitedUntil(provider, _memoryCache) ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds));
                        _logger.LogWarning("({Provider}) Rate limit exceeded; retrying in {TimeoutSeconds} seconds", provider, waitSeconds);
                        MemoryCacheHelper.SetRateLimitingForProvider(provider, _memoryCache, TimeSpan.FromSeconds(timeoutSeconds));
                        timeoutSeconds *= TimeoutIncrementMultiplier;
                        attempts++;
                        continue;
                    default:
                        _logger.LogWarning("({Provider}) {CallType} {Url} returned {StatusCode}", provider, callType, url, (int)responseMessage.StatusCode);
                        if (_logger.IsEnabled(LogLevel.Debug))
                            _logger.LogDebug("({Provider}) Response body: {ResponseBody}", provider, LogText.Truncate(await responseMessage.Content.ReadAsStringAsync()));
                        TrackerCallScope.Report(TrackerCallScope.FromStatus(responseMessage.StatusCode));
                        return null;
                }
            }
        }

        _logger.LogWarning("({Provider}) Gave up after {Count} attempts ({FailureKind}); if this repeats, link the account again", provider, attempts, lastFailure);
        TrackerCallScope.Report(lastFailure);
        return null;
    }

    /// <summary>
    /// AniList answers a revoked or invalid token with 400 "Invalid token" rather than 401, so that answer is read as
    /// Unauthorized: the sign-in is lost, not the request rejected.
    /// </summary>
    private static async Task<HttpStatusCode> EffectiveStatus(ApiName provider, HttpResponseMessage response) {
        if (provider != ApiName.AniList || response.StatusCode != HttpStatusCode.BadRequest)
            return response.StatusCode;
        string body = await response.Content.ReadAsStringAsync();
        return body.Contains("Invalid token", StringComparison.OrdinalIgnoreCase) ? HttpStatusCode.Unauthorized : response.StatusCode;
    }

    public enum CallType {
        GET,
        POST,
        PATCH,
        PUT,
        DELETE
    }
}
