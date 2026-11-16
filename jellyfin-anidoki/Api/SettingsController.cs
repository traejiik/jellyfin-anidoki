using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
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
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Jellyfin.Database.Implementations.Entities;

namespace jellyfin_anidoki.Api;

/// <summary>What the admin and user settings pages read and save: page parameters, a user's settings, the linked-account check and the anime-list folder check.</summary>
[ApiController]
[Route("AniDoki")]
public sealed class SettingsController : ControllerBase {
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServerApplicationHost _serverApplicationHost;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<SettingsController> _logger;
    private readonly IAsyncDelayer _delayer;

    public SettingsController(IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        IServerApplicationHost serverApplicationHost,
        IHttpContextAccessor httpContextAccessor,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IMemoryCache memoryCache) {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _serverApplicationHost = serverApplicationHost;
        _httpContextAccessor = httpContextAccessor;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _memoryCache = memoryCache;
        _logger = loggerFactory.CreateLogger<SettingsController>();
        _delayer = new Delayer();
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
        if (jellyfinUser == null)
            return Forbid();
        UserConfig? userConfig = Plugin.Instance?.PluginConfiguration.UserConfig?.FirstOrDefault(item => item.UserId == jellyfinUser.Id);
        if (userConfig == null) {
            _logger.LogWarning("User {UserId} has no AniDoki settings", jellyfinUser.Id);
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
                    _logger.LogWarning("({Provider}) Could not read the linked user of user {UserId}", ApiName.Kitsu, jellyfinUser.Id);
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
                AnnictApiCalls annictApiCalls;
                try {
                    annictApiCalls = new AnnictApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, userConfig);
                } catch (ArgumentNullException) {
                    _logger.LogWarning("({Provider}) Could not read the linked user of user {UserId}", ApiName.Annict, jellyfinUser.Id);
                    return StatusCode(500, "User could not be retrieved from API");
                }

                var annictApiCall = await annictApiCalls.GetCurrentUser();
                // A reply without a viewer (a GraphQL error has "data": null) is a failed read, not an account without a name.
                var annictViewer = annictApiCall?.AnnictSearchData?.Viewer;
                if (annictViewer == null) {
                    return StatusCode(500, "Authentication failed");
                }

                return new OkObjectResult(new MalApiCalls.User {
                    Name = annictViewer.username
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
                    _logger.LogWarning("({Provider}) Could not read the linked user of user {UserId}", ApiName.Shikimori, jellyfinUser.Id);
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
    public IActionResult FrontendParameters(ParameterInclude[]? includes, bool onlyConfiguredProviders = false) {
        return Ok(GetFrontendParameters(includes, onlyConfiguredProviders, false));
    }

    [Authorize]
    [HttpGet]
    [Route("user/user")]
    public async Task<ActionResult> GetUserUser(ApiName apiName, Guid user) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        var jellyfinUser = _userManager.GetUser(User, user);
        if (jellyfinUser == null)
            return Forbid();

        return await GetUser(apiName, jellyfinUser.Id.ToString());
    }

    [Authorize]
    [HttpGet]
    [Route("user/configuration")]
    public ActionResult GetConfigurationUser(Guid user) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        var jellyfinUser = _userManager.GetUser(User, user);
        if (jellyfinUser == null)
            return Forbid();

        var userConfig = Plugin.Instance?.PluginConfiguration.UserConfig?.FirstOrDefault(userConfig => userConfig.UserId == jellyfinUser.Id);
        return Ok(new UserConfigurationResponse(userConfig));
    }

    [Authorize]
    [HttpPut]
    [Route("user/configuration")]
    public ActionResult UpdateConfigurationUser(
        [FromQuery] Guid user,
        [FromBody] UserEditableConfig dto) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        var jellyfinUser = _userManager.GetUser(User, user);
        if (jellyfinUser == null)
            return Forbid();

        var plugin = Plugin.Instance;
        if (plugin?.PluginConfiguration == null)
            return StatusCode(500, "Plugin configuration not loaded");

        lock (plugin.PluginConfiguration) {
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
                _logger.LogWarning("User {UserId} asked to sync libraries they cannot open: {Libraries}", jellyfinUser.Id, String.Join(", ", libraries));
                return Forbid();
            }

            if (userConfig == null) {
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
            } else {
                userConfig.PlanToWatchOnly = dto.PlanToWatchOnly;
                userConfig.RewatchCompleted = dto.RewatchCompleted;
                userConfig.ShowLogNotifications = dto.ShowLogNotifications;
                userConfig.LibraryToCheck = libraries;
            }

            plugin.SaveConfiguration();

            return Ok(new UserConfigurationResponse(userConfig));
        }
    }

    [Authorize]
    [HttpGet]
    [Route("user/parameters")]
    public object GetFrontendParametersUser(ParameterInclude[]? includes) {
        if (!ConfigHelper.UserPagesEnabled())
            return NotFound();
        return GetFrontendParameters(includes, true, true);
    }

    private Parameters GetFrontendParameters(ParameterInclude[]? includes, bool onlyConfiguredProviders, bool onlyLibrariesUserHasAccessTo) {
        Parameters toReturn = new Parameters();

        if (includes == null || includes.Contains(ParameterInclude.ProviderList)) {
            toReturn.providerList = new List<ExpandoObject>();

            foreach (ApiName apiName in Enum.GetValues<ApiName>()) {
                if (onlyConfiguredProviders && apiName != ApiName.Kitsu && ConfigHelper.GetConfiguredOAuthProvider(apiName) == null)
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

        if (includes == null || includes.Contains(ParameterInclude.Libraries)) {
            Dictionary<Guid, string> libraries = new Dictionary<Guid, string>();
            if (onlyLibrariesUserHasAccessTo) {
                User? jellyfinUser = _userManager.GetUser(User, null);
                if (jellyfinUser != null) {
                    libraries = _libraryManager.GetLibrariesUserHasAccessTo(jellyfinUser);
                }
            } else {
                libraries = _libraryManager.GetVirtualFolders().ToDictionary(virtualFolderInfo => Guid.Parse(virtualFolderInfo.ItemId), virtualFolderInfo => virtualFolderInfo.Name);
            }
            // The anime library list shows each library's type and leaves out music, photos and the like.
            var types = _libraryManager.GetVirtualFolders()
                .Where(folder => Guid.TryParse(folder.ItemId, out _))
                .ToDictionary(folder => Guid.Parse(folder.ItemId), folder => folder.CollectionType?.ToString());
            toReturn.libraries = new List<ExpandoObject>();
            foreach (var library in libraries) {
                dynamic lib = new ExpandoObject();
                lib.Name = library.Value;
                lib.Id = library.Key;
                lib.Type = types.GetValueOrDefault(library.Key);
                toReturn.libraries.Add(lib);
            }
        }

        return toReturn;
    }
}
