using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Notifications.Discord;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.Kitsu;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Auth;

/// <summary>Links and unlinks tracker accounts, and serves the OAuth callback with its result and address-check pages.</summary>
[ApiController]
[Route("AniDoki")]
public sealed class AuthController : ControllerBase {
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServerApplicationHost _serverApplicationHost;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IUserManager _userManager;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<AuthController> _logger;
    private readonly IAsyncDelayer _delayer;

    public AuthController(IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IServerApplicationHost serverApplicationHost,
        IHttpContextAccessor httpContextAccessor,
        IUserManager userManager,
        IMemoryCache memoryCache) {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _serverApplicationHost = serverApplicationHost;
        _httpContextAccessor = httpContextAccessor;
        _userManager = userManager;
        _memoryCache = memoryCache;
        _logger = loggerFactory.CreateLogger<AuthController>();
        _delayer = new Delayer();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet]
    [Route("buildAuthorizeRequestUrl")]
    public string BuildAuthorizeRequestUrl(ApiName provider, string clientId, string clientSecret, string? url, Guid user) {
        return new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory, _memoryCache, _delayer, new ProviderApiAuth { ClientId = clientId, ClientSecret = clientSecret }, url).BuildAuthorizeRequestUrl(user, returnToAdmin: true);
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("callbackPreview")]
    public IActionResult CallbackPreview([FromQuery] string? address = null) {
        try {
            string baseAddress = string.IsNullOrWhiteSpace(address)
                ? CallbackUrlHelper.GetLocalBaseAddress(_serverApplicationHost, _httpContextAccessor.HttpContext)
                : address;
            string callbackUrl = CallbackUrlHelper.Build(baseAddress);
            return Ok(new { baseAddress, callbackUrl });
        } catch (ArgumentException error) {
            return BadRequest(error.Message);
        }
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("authorize")]
    public ActionResult AuthorizeProvider([FromQuery] ApiName provider, [FromQuery] Guid user) =>
        AuthorizeSavedProvider(provider, user, returnToAdmin: true);

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("passwordGrant")]
    public Task<IActionResult> PasswordGrantAuthenticationBody([FromBody] PasswordGrantRequest? request) =>
        PasswordGrantFromBody(request);

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("annictToken")]
    public IActionResult SetAnnictToken([FromQuery] Guid user, [FromBody] AnnictTokenRequest? request) {
        if (user == Guid.Empty || string.IsNullOrWhiteSpace(request?.Token))
            return BadRequest("A Jellyfin user and an Annict token are required");
        var jellyfinUser = _userManager.GetUser(User, user);
        if (jellyfinUser == null)
            return Forbid();
        lock (Plugin.Instance!.PluginConfiguration) {
            var configuration = GetOrCreateUserConfiguration(jellyfinUser.Id);
            if (configuration == null)
                return StatusCode(500, "Plugin configuration not loaded");
            var auth = configuration.UserApiAuth?.FirstOrDefault(auth => auth.Name == ApiName.Annict);
            if (auth == null)
                configuration.AddUserApiAuth(new UserApiAuth { Name = ApiName.Annict, AccessToken = request.Token, ConnectionGeneration = Guid.NewGuid() });
            else {
                auth.ConnectionGeneration = Guid.NewGuid();
                auth.AccessToken = request.Token;
            }
            Plugin.Instance!.SaveConfiguration();
            return Ok();
        }
    }

    // Not routed: credentials only arrive in a POST body, via PasswordGrantFromBody.
    internal async Task<IActionResult> PasswordGrantAuthentication(ApiName provider, string userId, string username, string password) {
        try {
            var token = await new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory, _memoryCache, _delayer, new ProviderApiAuth { ClientId = username, ClientSecret = password }).GetToken(Guid.Parse(userId));
            if (token == null)
                return StatusCode(500, "Could not authenticate");
        } catch (Exception e) {
            // The provider's response stays in the log; the settings page gets a sentence it can show as is.
            _logger.LogWarning("({Provider}) Sign-in with password failed: {ErrorMessage}", provider, e.Message);
            string name = Notifications.Discord.DiscordOutcomeText.ProviderName(provider);
            if (CallbackPage.ErrorCodeFrom(e.Message) == "invalid_grant")
                return BadRequest(provider == ApiName.Kitsu
                    ? "Kitsu didn’t accept that email and password. Kitsu signs in with your account email, not your username."
                    : $"{name} didn’t accept those sign-in details.");
            return StatusCode(500, $"Couldn’t sign in to {name}. Try again in a moment; if it keeps failing, the Jellyfin log has the details.");
        }

        if (provider == ApiName.Kitsu) {
            // An admin settings save can replace the configuration with one that has no user list yet.
            var userConfig = Plugin.Instance?.PluginConfiguration.UserConfig?.FirstOrDefault(item => item.UserId == Guid.Parse(userId));

            if (userConfig != null) {
                KitsuApiCalls kitsuApiCalls = new KitsuApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, userConfig);
                var kitsuUserConfig = await kitsuApiCalls.GetUserInformation();
                if (kitsuUserConfig == null)
                    return StatusCode(500, "Could not authenticate");
                userConfig.KeyPairs ??= new List<KeyPairs>();
                var existingKeyPair = userConfig.KeyPairs.FirstOrDefault(item => item.Key == "KitsuUserId");
                if (existingKeyPair != null) {
                    existingKeyPair.Value = kitsuUserConfig.Id.ToString();
                } else {
                    userConfig.KeyPairs.Add(new KeyPairs { Key = "KitsuUserId", Value = kitsuUserConfig.Id.ToString() });
                }

                Plugin.Instance?.SaveConfiguration();
            }
        }

        return Ok();
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost]
    [Route("deauthenticate")]
    public IActionResult Deauthenticate([FromQuery] Guid user, [FromQuery] ApiName apiName) =>
        DeauthenticateProvidedUser(user, apiName);

    [Authorize]
    [HttpGet]
    [Route("user/buildAuthorizeRequestUrl")]
    public ActionResult BuildAuthorizeRequestUrlUser(ApiName provider, Guid user) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        return AuthorizeSavedProvider(provider, user);
    }

    [Authorize]
    [HttpPost("user/passwordGrant")]
    public Task<IActionResult> PasswordGrantAuthenticationUserBody([FromBody] PasswordGrantRequest? request) {
        if (!ConfigHelper.UserPagesEnabled())
            return Task.FromResult<IActionResult>(NotFound());
        return PasswordGrantFromBody(request);
    }

    [Authorize]
    [HttpPost]
    [Route("user/deauthenticate")]
    public IActionResult DeauthenticateUser([FromQuery] Guid user, [FromQuery] ApiName apiName) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        return DeauthenticateProvidedUser(user, apiName);
    }

    [AllowAnonymous]
    [HttpGet]
    [Route("authCallback")]
    public async Task<IActionResult> AuthCallback(string? code, string? state, string? error = null) {
        StoredState? storedState = string.IsNullOrEmpty(state) ? null : MemoryCacheHelper.ConsumeState(_memoryCache, state);
        CallbackState.TryReadOrigin(state, out _, out ApiName? markedProvider);
        if (storedState == null) {
            bool elsewhere = CallbackState.IsFromOtherServer(state, _serverApplicationHost.SystemId);
            _logger.LogInformation("The sign-in callback for {Provider} had no matching link: {Reason}", markedProvider, elsewhere ? "issued by another server" : "expired or already used");
            return CallbackPageResult(elsewhere ? CallbackOutcome.WrongServer : CallbackOutcome.Expired, markedProvider, null);
        }

        string? jellyfinUser = _userManager.GetUserById(storedState.UserId)?.Username;
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) {
            string? errorCode = CallbackPage.SafeErrorCode(error);
            _logger.LogInformation("({Provider}) Sign-in was not approved: {ErrorCode}", storedState.ApiName, errorCode ?? "no code returned");
            return CallbackPageResult(error == "access_denied" ? CallbackOutcome.Declined : CallbackOutcome.ProviderError, storedState.ApiName, jellyfinUser, errorCode, storedState.ReturnToAdmin);
        }
        try {
            var token = await new ApiAuthentication(storedState.ApiName, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory, _memoryCache, _delayer).GetToken(storedState.UserId, code, codeVerifier: storedState.CodeVerifier);
            if (token == null)
                return CallbackPageResult(CallbackOutcome.ProviderError, storedState.ApiName, jellyfinUser, returnToAdmin: storedState.ReturnToAdmin);
        } catch (Exception exception) {
            // this endpoint is anonymous; only the OAuth error code reaches the page, the response stays in the server log
            _logger.LogWarning("({Provider}) Could not complete sign-in: {ErrorMessage}", storedState.ApiName, exception.Message);
            return CallbackPageResult(CallbackOutcome.ProviderError, storedState.ApiName, jellyfinUser, CallbackPage.ErrorCodeFrom(exception.Message), storedState.ReturnToAdmin);
        }
        if (!string.IsNullOrEmpty(Plugin.Instance?.PluginConfiguration.callbackRedirectUrl)) {
            string replacedCallbackRedirectUrl = Plugin.Instance.PluginConfiguration.callbackRedirectUrl.Replace("{{LocalIpAddress}}", Request.HttpContext.Connection.LocalIpAddress != null ? Request.HttpContext.Connection.LocalIpAddress.ToString() : "localhost")
                .Replace("{{LocalPort}}", _serverApplicationHost.ListenWithHttps ? _serverApplicationHost.HttpsPort.ToString() : _serverApplicationHost.HttpPort.ToString());

            if (Uri.TryCreate(replacedCallbackRedirectUrl, UriKind.Absolute, out _)) {
                return Redirect(replacedCallbackRedirectUrl);
            } else {
                _logger.LogWarning("The callback redirect URL {Url} is not valid; showing the result page instead", replacedCallbackRedirectUrl);
            }
        }

        return CallbackPageResult(CallbackOutcome.Linked, storedState.ApiName, jellyfinUser, returnToAdmin: storedState.ReturnToAdmin);
    }

    private ContentResult CallbackPageResult(CallbackOutcome outcome, ApiName? provider, string? jellyfinUser, string? errorCode = null, bool returnToAdmin = false) {
        var configuration = Plugin.Instance?.PluginConfiguration;
        string callbackBase = string.IsNullOrWhiteSpace(configuration?.callbackUrl)
            ? CallbackUrlHelper.GetLocalBaseAddress(_serverApplicationHost, HttpContext)
            : configuration.callbackUrl;
        // Relative to {base}/AniDoki/authCallback, so a Jellyfin base path needs no handling here.
        string settingsHref = configuration?.enableUserPages == true && !returnToAdmin
            ? "../web/index.html#/userpluginsettings.html?pageUrl=/AniDoki/settings"
            : "../web/index.html#/configurationpage?name=AniDoki";
        var model = new CallbackPageModel(outcome, provider, jellyfinUser, ResultPageAssets, settingsHref,
            CallbackUrlHelper.Build(callbackBase), errorCode, MemoryCacheHelper.LinkExpiryMinutes(configuration?.authenticationLinkExpireTimeMinutes));
        Response.Headers.CacheControl = "no-store";
        return new ContentResult {
            Content = CallbackPage.Render(model),
            ContentType = "text/html; charset=utf-8",
            StatusCode = CallbackPage.StatusCode(outcome)
        };
    }

    [AllowAnonymous]
    [HttpGet]
    [Route("apiUrlTest")]
    public ContentResult ApiUrlTest() {
        Response.Headers.CacheControl = "no-store";
        return new ContentResult {
            Content = CallbackPage.RenderAddressCheck(ResultPageAssets),
            ContentType = "text/html; charset=utf-8",
            StatusCode = 200
        };
    }

    // Relative to {base}/AniDoki/, like the pages that use it.
    private static string ResultPageAssets => $"assets/v/{typeof(Plugin).Assembly.ManifestModule.ModuleVersionId:N}/";

    private ActionResult AuthorizeSavedProvider(ApiName provider, Guid user, bool returnToAdmin = false) {
        if (user == Guid.Empty)
            return BadRequest("A Jellyfin user is required");
        var jellyfinUser = _userManager.GetUser(User, user);
        if (jellyfinUser == null)
            return Forbid();
        var auth = ConfigHelper.GetConfiguredOAuthProvider(provider);
        if (auth == null)
            return BadRequest("Provider does not support OAuth or its saved app settings are incomplete");
        try {
            var authentication = new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost,
                _httpContextAccessor, _loggerFactory, _memoryCache, _delayer, auth);
            string url = authentication.BuildAuthorizeRequestUrl(jellyfinUser.Id, returnToAdmin);
            if (GetOrCreateUserConfiguration(jellyfinUser.Id) == null)
                return StatusCode(500, "Plugin configuration not loaded");
            return Ok(url);
        } catch (ArgumentException error) {
            return BadRequest(error.Message);
        }
    }

    private async Task<IActionResult> PasswordGrantFromBody(PasswordGrantRequest? request) {
        if (request == null || request.Provider != ApiName.Kitsu || request.User == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("Kitsu provider, a Jellyfin user, a username, and a password are required");
        var jellyfinUser = _userManager.GetUser(User, request.User);
        if (jellyfinUser == null)
            return Forbid();
        if (GetOrCreateUserConfiguration(jellyfinUser.Id) == null)
            return StatusCode(500, "Plugin configuration not loaded");
        return await PasswordGrantAuthentication(request.Provider, jellyfinUser.Id.ToString(), request.Username, request.Password);
    }

    private static UserConfig? GetOrCreateUserConfiguration(Guid userId) {
        var plugin = Plugin.Instance;
        if (plugin == null)
            return null;
        var configuration = plugin.PluginConfiguration;
        lock (configuration) {
            configuration.UserConfig ??= Array.Empty<UserConfig>();
            var existing = configuration.UserConfig.FirstOrDefault(config => config.UserId == userId);
            if (existing != null)
                return existing;
            var created = new UserConfig { UserId = userId };
            configuration.UserConfig = configuration.UserConfig.Append(created).ToArray();
            plugin.SaveConfiguration();
            return created;
        }
    }

    private IActionResult DeauthenticateProvidedUser(Guid user, ApiName apiName) {
        var jellyfinUser = _userManager.GetUser(User, user);
        if (jellyfinUser == null)
            return Forbid();

        (bool success, string? reason) deauthenticateUser = ConfigHelper.DeauthenticateUser(jellyfinUser.Id, apiName);
        if (deauthenticateUser.success) {
            return Ok();
        } else {
            _logger.LogError("({Provider}) Could not unlink the account of user {UserId}: {Reason}", apiName, jellyfinUser.Id, deauthenticateUser.reason);
            return StatusCode(500);
        }
    }
}
