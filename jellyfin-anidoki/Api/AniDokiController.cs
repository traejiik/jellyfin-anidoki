#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Api.Anilist;
using jellyfin_anidoki.Api.Annict;
using jellyfin_anidoki.Api.Kitsu;
using jellyfin_anidoki.Api.Shikimori;
using jellyfin_anidoki.Api.Simkl;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Extensions;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Jellyfin.Database.Implementations.Entities;

namespace jellyfin_anidoki.Api {
    [ApiController]
    [Route("[controller]")]
    public class AniDokiController : ControllerBase {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServerApplicationHost _serverApplicationHost;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IApplicationPaths _applicationPaths;
        private readonly IUserDataManager _userDataManager;
        private readonly ILogger<AniDokiController> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly IAsyncDelayer _delayer;

        public AniDokiController(IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            IServerApplicationHost serverApplicationHost,
            IHttpContextAccessor httpContextAccessor,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IApplicationPaths applicationPaths,
            IUserDataManager userDataManager,
            IMemoryCache memoryCache) {
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
            _serverApplicationHost = serverApplicationHost;
            _httpContextAccessor = httpContextAccessor;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _applicationPaths = applicationPaths;
            _userDataManager = userDataManager;
            _logger = loggerFactory.CreateLogger<AniDokiController>();
            _memoryCache = memoryCache;
            _delayer = new Delayer();
        }

        private bool UserPagesEnabled()
        {
            return Plugin.Instance?.PluginConfiguration.enableUserPages == true;
        }

        [Authorize(Policy = Policies.RequiresElevation)]
        [HttpGet]
        [Route("buildAuthorizeRequestUrl")]
        public string BuildAuthorizeRequestUrl(ApiName provider, string clientId, string clientSecret, string? url, Guid user) {
            return new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory, _memoryCache, _delayer, new ProviderApiAuth { ClientId = clientId, ClientSecret = clientSecret }, url).BuildAuthorizeRequestUrl(user);
        }

        [Authorize(Policy = Policies.RequiresElevation)]
        [HttpGet("callbackPreview")]
        public IActionResult CallbackPreview([FromQuery] string? address = null)
        {
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
            AuthorizeSavedProvider(provider, user);

        [Authorize(Policy = Policies.RequiresElevation)]
        [HttpPost("passwordGrant")]
        public Task<IActionResult> PasswordGrantAuthenticationBody([FromBody] PasswordGrantRequest? request) =>
            PasswordGrantFromBody(request);

        [Authorize(Policy = Policies.RequiresElevation)]
        [HttpPost("annictToken")]
        public IActionResult SetAnnictToken([FromQuery] Guid user, [FromBody] AnnictTokenRequest? request)
        {
            if (user == Guid.Empty || string.IsNullOrWhiteSpace(request?.Token))
                return BadRequest("A Jellyfin user and an Annict token are required");
            var jellyfinUser = _userManager.GetUser(User, user);
            if (jellyfinUser == null) return Forbid();
            var configuration = GetOrCreateUserConfiguration(jellyfinUser.Id);
            if (configuration == null) return StatusCode(500, "Plugin configuration not loaded");
            var auth = configuration.UserApiAuth?.FirstOrDefault(auth => auth.Name == ApiName.Annict);
            if (auth == null)
                configuration.AddUserApiAuth(new UserApiAuth { Name = ApiName.Annict, AccessToken = request.Token });
            else
                auth.AccessToken = request.Token;
            Plugin.Instance!.SaveConfiguration();
            return Ok();
        }

        [AllowAnonymous]
        [HttpGet("assets/{asset}")]
        public IActionResult GetAsset([FromRoute] string asset)
        {
            string? resource = asset switch {
                "styles.css" => "Anidoki.css",
                "notifications.js" => "NotificationsJs.js",
                "notification-state.js" => "NotificationStateJs.js",
                "common.js" => "CommonJs.js",
                "config-state.js" => "ConfigStateJs.js",
                "user-settings.js" => "ConfigPageUserJs.js",
                "provider-anilist.svg" => "Images.provider-anilist.svg",
                "provider-mal.svg" => "Images.provider-mal.svg",
                "provider-kitsu.svg" => "Images.provider-kitsu.svg",
                "provider-annict.png" => "Images.provider-annict.png",
                "provider-shikimori.svg" => "Images.provider-shikimori.svg",
                "provider-simkl.svg" => "Images.provider-simkl.svg",
                _ => null
            };
            if (resource == null) return NotFound();
            var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Configuration.{resource}");
            if (stream == null) return NotFound();
            Response.Headers.CacheControl = "no-store";
            return File(stream, MimeTypes.GetMimeType(resource));
        }

        [Authorize(Policy = Policies.RequiresElevation)]
        [HttpGet]
        [Route("testAnimeListSaveLocation")]
        public async Task<IActionResult> TestAnimeSaveLocation(string saveLocation) {
            if (String.IsNullOrEmpty(saveLocation))
                return BadRequest("Save location is empty");

            try {
                await using (System.IO.File.Create(
                                 Path.Combine(
                                     saveLocation,
                                     Path.GetRandomFileName()
                                 ),
                                 1,
                                 FileOptions.DeleteOnClose)) {
                }

                return Ok(string.Empty);
            } catch (Exception e) {
                return BadRequest(e.Message);
            }
        }

        [Authorize(Policy = Policies.RequiresElevation)]
        [HttpGet]
        [Route("passwordGrant")]
        public async Task<IActionResult> PasswordGrantAuthentication(ApiName provider, string userId, string username, string password) {
            try {
                var token = await new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory, _memoryCache, _delayer, new ProviderApiAuth { ClientId = username, ClientSecret = password }).GetToken(Guid.Parse(userId));
                if (token == null)
                    return StatusCode(500, "Could not authenticate");
            } catch (Exception e) {
                return StatusCode(500, $"Could not authenticate; {e.Message}");
            }

            if (provider == ApiName.Kitsu) {
                var userConfig = Plugin.Instance?.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == Guid.Parse(userId));

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
        [HttpGet]
        [Route("user")]
        public async Task<ActionResult> GetUser(ApiName apiName, string? userId = null, [FromQuery] Guid? user = null) {
            Guid? targetId = user;
            if (!string.IsNullOrWhiteSpace(userId)) {
                if (!Guid.TryParse(userId, out var legacyId) || (targetId.HasValue && targetId.Value != legacyId))
                    return BadRequest("Select one valid Jellyfin user");
                targetId = legacyId;
            }
            if (!targetId.HasValue || targetId.Value == Guid.Empty)
                return BadRequest("A Jellyfin user is required");
            var jellyfinUser = _userManager.GetUser(User, targetId);
            if (jellyfinUser == null) return Forbid();
            UserConfig? userConfig = Plugin.Instance?.PluginConfiguration.UserConfig?.FirstOrDefault(item => item.UserId == jellyfinUser.Id);
            if (userConfig == null) {
                _logger.LogError("User not found in config");
                return StatusCode(500, "User not found in config");
            }

            switch (apiName) {
                case ApiName.Mal:
                    MalApiCalls malApiCalls = new MalApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, userConfig);

                    MalApiCalls.User? malUser = await malApiCalls.GetUserInformation();
                    return malUser != null ? new OkObjectResult(malUser) : StatusCode(500, "Authentication failed");
                case ApiName.AniList:
                    AniListApiCalls aniListApiCalls = new AniListApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, userConfig);

                    AniListViewer.Viewer? viewer = await aniListApiCalls.GetCurrentUser();
                    if (viewer == null) {
                        return StatusCode(500, "Authentication failed");
                    }

                    return new OkObjectResult(new MalApiCalls.User {
                        Name = viewer.Name
                    });
                case ApiName.Kitsu:
                    KitsuApiCalls kitsuApiCalls;
                    try {
                        kitsuApiCalls = new KitsuApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, userConfig);
                    } catch (ArgumentNullException) {
                        _logger.LogError("User could not be retrieved from API");
                        return StatusCode(500, "User could not be retrieved from API");
                    }

                    var apiCall = await kitsuApiCalls.GetUserInformation();
                    if (apiCall == null) {
                        return StatusCode(500, "Authentication failed");
                    }

                    return new OkObjectResult(new MalApiCalls.User {
                        Name = apiCall.Name
                    });
                case ApiName.Annict:
                    // sleep the thread for a short amount of time to let the user config save
                    Thread.Sleep(100);
                    AnnictApiCalls annictApiCalls;
                    try {
                        annictApiCalls = new AnnictApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, userConfig);
                    } catch (ArgumentNullException) {
                        _logger.LogError("User could not be retrieved from API");
                        return StatusCode(500, "User could not be retrieved from API");
                    }

                    var annictApiCall = await annictApiCalls.GetCurrentUser();
                    if (annictApiCall == null) {
                        return StatusCode(500, "Authentication failed");
                    }

                    return new OkObjectResult(new MalApiCalls.User {
                        Name = annictApiCall.AnnictSearchData.Viewer.username
                    });
                case ApiName.Shikimori:
                    string? shikimoriAppName = ConfigHelper.GetShikimoriAppName(_logger);
                    if (string.IsNullOrEmpty(shikimoriAppName)) {
                        return StatusCode(500, "No App Name");
                    }

                    ShikimoriApiCalls shikimoriApiCalls = new ShikimoriApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, new Dictionary<string, string> { { "User-Agent", shikimoriAppName } }, userConfig);

                    ShikimoriApiCalls.User? shikimoriUserApiCall = await shikimoriApiCalls.GetUserInformation();
                    if (shikimoriUserApiCall != null) {
                        return new OkObjectResult(new MalApiCalls.User {
                            Name = shikimoriUserApiCall.Name
                        });
                    } else {
                        _logger.LogError("User could not be retrieved from API");
                        return StatusCode(500, "User could not be retrieved from API");
                    }
                case ApiName.Simkl:
                    string? simklClientId = ConfigHelper.GetSimklClientId(_logger);
                    if (string.IsNullOrEmpty(simklClientId)) {
                        return StatusCode(500, "No Client ID");
                    }

                    var simklApiCalls = new SimklApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, new Dictionary<string, string> { { "simkl-api-key", simklClientId } }, userConfig);

                    if (await simklApiCalls.GetLastActivity()) {
                        return new OkObjectResult(new MalApiCalls.User {
                            Name = null
                        });
                    } else {
                        return StatusCode(500, "Not authenticated");
                    }
            }

            throw new Exception("Provider not supported.");
        }

        [Authorize(Policy = Policies.RequiresElevation)]
        [HttpGet]
        [Route("parameters")]
        public IActionResult FrontendParameters(ParameterInclude[]? includes, bool onlyConfiguredProviders = false)
        {
            return Ok(GetFrontendParameters(includes, onlyConfiguredProviders, false));
        }

        [Authorize(Policy = Policies.RequiresElevation)]
        [HttpPost]
        [Route("sync")]
        public Task Sync(ApiName provider, string userId, SyncHelper.Status status, SyncAction syncAction) {
            switch (syncAction) {
                case SyncAction.UpdateProvider:
                    SyncProviderFromLocal syncProviderFromLocal = new SyncProviderFromLocal(_userManager, _libraryManager, _loggerFactory, _httpClientFactory, _applicationPaths, _memoryCache, _delayer, userId);
                    return syncProviderFromLocal.SyncFromLocal();
                case SyncAction.UpdateJellyfin:
                    Sync sync = new Sync(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _userManager, _libraryManager, _applicationPaths, _userDataManager, _memoryCache, _delayer, provider, status);
                    return sync.SyncFromProvider(userId);
            }

            return Task.CompletedTask;
        }

        [Authorize(Policy = Policies.RequiresElevation)]
        [HttpGet]
        [Route("deauthenticate")]
        public IActionResult Deauthenticate([FromQuery] Guid user, [FromQuery] ApiName apiName) =>
            DeauthenticateProvidedUser(user, apiName);

        // The following endpoints are user-specific versions of the above endpoints (do not require elevated access)

        #region User Endpoints
        
        [Authorize]
        [HttpGet]
        [Route("user/buildAuthorizeRequestUrl")]
        public ActionResult BuildAuthorizeRequestUrlUser(ApiName provider, Guid user) {
            if (!UserPagesEnabled()) return NotFound();
            return AuthorizeSavedProvider(provider, user);
        }

        [Authorize]
        [HttpPost("user/passwordGrant")]
        public Task<IActionResult> PasswordGrantAuthenticationUserBody([FromBody] PasswordGrantRequest? request)
        {
            if (!UserPagesEnabled()) return Task.FromResult<IActionResult>(NotFound());
            return PasswordGrantFromBody(request);
        }

        [Authorize]
        [HttpGet]
        [Route("user/passwordGrant")]
        public async Task<IActionResult> PasswordGrantAuthenticationUser(ApiName provider, [FromQuery] Guid user, string username, string password) {
            if (!UserPagesEnabled()) return NotFound();
            var jellyfinUser = _userManager.GetUser(User, user);
            if (jellyfinUser == null) return Forbid();
            
            return await PasswordGrantAuthentication(provider, jellyfinUser.Id.ToString(), username, password);
        }

        [Authorize]
        [HttpGet]
        [Route("user/user")]
        public async Task<ActionResult> GetUserUser(ApiName apiName, Guid user) {
            if (!UserPagesEnabled()) return NotFound();
            var jellyfinUser = _userManager.GetUser(User, user);
            if (jellyfinUser == null) return Forbid();

            return await GetUser(apiName, jellyfinUser.Id.ToString());
        }

        [Authorize]
        [HttpGet]
        [Route("user/configuration")]
        public ActionResult GetConfigurationUser(Guid user) {
            if (!UserPagesEnabled()) return NotFound();
            var jellyfinUser = _userManager.GetUser(User, user);
            if (jellyfinUser == null) return Forbid();

            var userConfig = Plugin.Instance?.PluginConfiguration.UserConfig?.FirstOrDefault(userConfig => userConfig.UserId == jellyfinUser.Id);
            return Ok(new UserConfigurationResponse(userConfig));
        }

        [Authorize]
        [HttpPut]
        [Route("user/configuration")]
        public ActionResult UpdateConfigurationUser(
            [FromQuery] Guid user,
            [FromBody] UserEditableConfig dto)
        {
            if (!UserPagesEnabled()) return NotFound();
            var jellyfinUser = _userManager.GetUser(User, user);
            if (jellyfinUser == null)
                return Forbid();

            var plugin = Plugin.Instance;
            if (plugin?.PluginConfiguration == null)
                return StatusCode(500, "Plugin configuration not loaded");

            var config = plugin.PluginConfiguration;
            config.UserConfig ??= Array.Empty<UserConfig>();

            var userConfig = config.UserConfig
                .FirstOrDefault(x => x.UserId == jellyfinUser.Id);

            var libraries = dto.LibraryToCheck ?? Array.Empty<string>();
            HashSet<Guid> libraryIds = [];
            foreach (var library in libraries) {
                if (!Guid.TryParse(library, out var libraryId) || libraryId == Guid.Empty)
                    return BadRequest("Library IDs must be valid, non-empty GUIDs");
                libraryIds.Add(libraryId);
            }

            if (!_libraryManager.UserHasAccessToLibraries(libraryIds, jellyfinUser)) {
                _logger.LogError($"User {jellyfinUser.Id} does not have access to requested libraries ({String.Join(", ", libraries)})");
                return Forbid();
            }

            if (userConfig == null)
            {
                userConfig = new UserConfig {
                    UserId = jellyfinUser.Id,
                    PlanToWatchOnly = dto.PlanToWatchOnly,
                    RewatchCompleted = dto.RewatchCompleted,
                    ShowLogNotifications = dto.ShowLogNotifications,
                    LibraryToCheck = libraries
                };

                config.UserConfig = config.UserConfig
                    .Append(userConfig)
                    .ToArray();
            }
            else
            {
                userConfig.PlanToWatchOnly = dto.PlanToWatchOnly;
                userConfig.RewatchCompleted = dto.RewatchCompleted;
                userConfig.ShowLogNotifications = dto.ShowLogNotifications;
                userConfig.LibraryToCheck = libraries;
            }

            plugin.SaveConfiguration();

            return Ok(new UserConfigurationResponse(userConfig));
        }

        [Authorize]
        [HttpGet]
        [Route("user/parameters")]
        public object GetFrontendParametersUser(ParameterInclude[]? includes) {
            if (!UserPagesEnabled()) return NotFound();
            return GetFrontendParameters(includes, true, true);
        }

        [Authorize]
        [HttpGet]
        [Route("user/deauthenticate")]
        public IActionResult DeauthenticateUser([FromQuery] Guid user, [FromQuery] ApiName apiName) {
            if (!UserPagesEnabled()) return NotFound();
            return DeauthenticateProvidedUser(user, apiName);
        }

        [Authorize]
        [HttpGet("{viewName}")]
        public ActionResult GetView([FromRoute] string viewName)
        {
            if (Plugin.Instance == null)
            {
                return BadRequest("No plugin instance found");
            }

            if (!UserPagesEnabled()) return NotFound();

            IEnumerable<PluginPageInfo> pages = Plugin.Instance.GetViews();

            if (pages == null)
            {
                return NotFound("Pages is null or empty");
            }

            PluginPageInfo? view = pages.FirstOrDefault(pageInfo => pageInfo?.Name == viewName, null);

            if (view == null)
            {
                return NotFound("No matching view found");
            }

            Stream? stream = Plugin.Instance.GetType().Assembly.GetManifestResourceStream(view.EmbeddedResourcePath);

            if (stream == null)
            {
                _logger.LogError("Failed to get resource {Resource}", view.EmbeddedResourcePath);
                return NotFound();
            }

            return File(stream, MimeTypes.GetMimeType(view.EmbeddedResourcePath));
        }
        
        #endregion


        // The following endpoint are allowed to be accessed anonymously (do not require any authentication)

        [AllowAnonymous]
        [HttpGet]
        [Route("authCallback")]
        public async Task<IActionResult> AuthCallback(string code, string? state) {
            if (state == null) return BadRequest("State is empty");
            StoredState? storedState = MemoryCacheHelper.ConsumeState(_memoryCache, state);
            if (storedState == null) return BadRequest("User not found or link already used/expired, try again");
            try {
                var token = await new ApiAuthentication(storedState.ApiName, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory, _memoryCache, _delayer).GetToken(storedState.UserId, code);
                if (token == null) return StatusCode(500, "Could not authenticate");
            } catch (Exception error) {
                return StatusCode(500, $"Could not authenticate; {error.Message}");
            }
            if (!string.IsNullOrEmpty(Plugin.Instance?.PluginConfiguration.callbackRedirectUrl)) {
                string replacedCallbackRedirectUrl = Plugin.Instance.PluginConfiguration.callbackRedirectUrl.Replace("{{LocalIpAddress}}", Request.HttpContext.Connection.LocalIpAddress != null ? Request.HttpContext.Connection.LocalIpAddress.ToString() : "localhost")
                    .Replace("{{LocalPort}}", _serverApplicationHost.ListenWithHttps ? _serverApplicationHost.HttpsPort.ToString() : _serverApplicationHost.HttpPort.ToString());

                if (Uri.TryCreate(replacedCallbackRedirectUrl, UriKind.Absolute, out _)) {
                    return Redirect(replacedCallbackRedirectUrl);
                } else {
                    _logger.LogWarning($"Invalid redirect URL ({replacedCallbackRedirectUrl}), skipping redirect.");
                }
            }

            return new ObjectResult("Success! Received access token! You can test your authentication in AniDoki Configuration!") { StatusCode = 200 };
        }

        [AllowAnonymous]
        [HttpGet]
        [Route("apiUrlTest")]
        public string ApiUrlTest() {
            return "This is the correct URL.";
        }

        private Parameters GetFrontendParameters(ParameterInclude[]? includes, bool onlyConfiguredProviders, bool onlyLibrariesUserHasAccessTo) {
            Parameters toReturn = new Parameters();

            if (includes == null || includes.Contains(ParameterInclude.ProviderList))
            {
                toReturn.providerList = new List<ExpandoObject>();

                foreach (ApiName apiName in Enum.GetValues<ApiName>())
                {
                    if (onlyConfiguredProviders && apiName != ApiName.Kitsu && GetConfiguredOAuthProvider(apiName) == null)
                        continue;

                    dynamic provider = new ExpandoObject();
                    provider.Name = apiName.GetType()
                        .GetMember(apiName.ToString())
                        .First()
                        .GetCustomAttribute<DisplayAttribute>()
                        ?.GetName();
                    provider.Key = apiName;

                    toReturn.providerList.Add(provider);
                }
            }

            if (includes == null || includes.Contains(ParameterInclude.LocalIpAddress))
                toReturn.localIpAddress = Request.HttpContext.Connection.LocalIpAddress?.ToString() ?? "localhost";

            if (includes == null || includes.Contains(ParameterInclude.LocalPort))
                toReturn.localPort = _serverApplicationHost.ListenWithHttps
                    ? _serverApplicationHost.HttpsPort
                    : _serverApplicationHost.HttpPort;

            if (includes == null || includes.Contains(ParameterInclude.Https))
                toReturn.https = _serverApplicationHost.ListenWithHttps;

            if (includes == null || includes.Contains(ParameterInclude.Libraries))
            {
                Dictionary<Guid, string> libraries = new Dictionary<Guid, string>();
                if (onlyLibrariesUserHasAccessTo) {
                    User? jellyfinUser = _userManager.GetUser(User, null);
                    if (jellyfinUser != null) {
                        libraries = _libraryManager.GetLibrariesUserHasAccessTo(jellyfinUser);
                    }
                } else {
                    libraries = _libraryManager.GetVirtualFolders().ToDictionary(virtualFolderInfo => Guid.Parse(virtualFolderInfo.ItemId), virtualFolderInfo => virtualFolderInfo.Name);
                }
                toReturn.libraries = new List<ExpandoObject>();
                foreach (var library in libraries)
                {
                    dynamic lib = new ExpandoObject();
                    lib.Name = library.Value;
                    lib.Id = library.Key;
                    toReturn.libraries.Add(lib);
                }
            }

            return toReturn;
        }

        private static ProviderApiAuth? GetConfiguredOAuthProvider(ApiName provider)
        {
            if (provider is not (ApiName.Mal or ApiName.AniList or ApiName.Shikimori or ApiName.Simkl))
                return null;
            var configuration = Plugin.Instance?.PluginConfiguration;
            var auth = configuration?.ProviderApiAuth?.FirstOrDefault(auth => auth.Name == provider);
            if (string.IsNullOrWhiteSpace(auth?.ClientId) || string.IsNullOrWhiteSpace(auth.ClientSecret) ||
                (provider == ApiName.Shikimori && string.IsNullOrWhiteSpace(configuration?.shikimoriAppName)))
                return null;
            return auth;
        }

        private ActionResult AuthorizeSavedProvider(ApiName provider, Guid user)
        {
            if (user == Guid.Empty) return BadRequest("A Jellyfin user is required");
            var jellyfinUser = _userManager.GetUser(User, user);
            if (jellyfinUser == null) return Forbid();
            var auth = GetConfiguredOAuthProvider(provider);
            if (auth == null) return BadRequest("Provider does not support OAuth or its saved app settings are incomplete");
            try {
                var authentication = new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost,
                    _httpContextAccessor, _loggerFactory, _memoryCache, _delayer, auth);
                string url = authentication.BuildAuthorizeRequestUrl(jellyfinUser.Id);
                if (GetOrCreateUserConfiguration(jellyfinUser.Id) == null)
                    return StatusCode(500, "Plugin configuration not loaded");
                return Ok(url);
            } catch (ArgumentException error) {
                return BadRequest(error.Message);
            }
        }

        private async Task<IActionResult> PasswordGrantFromBody(PasswordGrantRequest? request)
        {
            if (request == null || request.Provider != ApiName.Kitsu || request.User == Guid.Empty ||
                string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest("Kitsu provider, a Jellyfin user, a username, and a password are required");
            var jellyfinUser = _userManager.GetUser(User, request.User);
            if (jellyfinUser == null) return Forbid();
            if (GetOrCreateUserConfiguration(jellyfinUser.Id) == null)
                return StatusCode(500, "Plugin configuration not loaded");
            return await PasswordGrantAuthentication(request.Provider, jellyfinUser.Id.ToString(), request.Username, request.Password);
        }

        private static UserConfig? GetOrCreateUserConfiguration(Guid userId)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return null;
            var configuration = plugin.PluginConfiguration;
            configuration.UserConfig ??= Array.Empty<UserConfig>();
            var existing = configuration.UserConfig.FirstOrDefault(config => config.UserId == userId);
            if (existing != null) return existing;
            var created = new UserConfig { UserId = userId, LibraryToCheck = Array.Empty<string>() };
            configuration.UserConfig = configuration.UserConfig.Append(created).ToArray();
            plugin.SaveConfiguration();
            return created;
        }

        private IActionResult DeauthenticateProvidedUser(Guid user, ApiName apiName) {
            var jellyfinUser = _userManager.GetUser(User, user);
            if (jellyfinUser == null) return Forbid();
            
            (bool success, string? reason) deauthenticateUser = ConfigHelper.DeauthenticateUser(jellyfinUser.Id, apiName);
            if (deauthenticateUser.success) {
                return Ok();
            } else {
                _logger.LogError($"Error while deauthenticating user {jellyfinUser.Id}: {deauthenticateUser.reason}");
                return StatusCode(500);
            }
        }
    }
}
