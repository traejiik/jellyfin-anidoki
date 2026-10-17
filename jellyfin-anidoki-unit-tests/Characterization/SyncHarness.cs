using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Notifications;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace jellyfin_anidoki_unit_tests.Characterization;

/// <summary>
/// Runs <see cref="UpdateProviderStatus"/> against fake trackers and formats what each provider received and reported.
/// </summary>
internal sealed class SyncHarness : IDisposable {
    /// <summary>The ID the ARM fixture returns for every provider, and the ID most fixtures give their entry.</summary>
    public const int EntryId = 500;

    public static readonly ApiName[] AllProviders = [ApiName.Mal, ApiName.AniList, ApiName.Kitsu, ApiName.Annict, ApiName.Shikimori, ApiName.Simkl];

    private readonly string _directory;
    private readonly Plugin _plugin;
    private readonly IHttpClientFactory _http;
    private readonly Dictionary<ApiName, FakeTracker> _trackers = new();

    public LibraryFixture Library { get; } = new();

    public PluginConfiguration Configuration => _plugin.Configuration;

    public SyncHarness(string? dataPath = null) {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-characterization-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(_directory);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
        paths.SetupGet(p => p.DataPath).Returns(dataPath ?? _directory);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration());
        _plugin = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance);
        _plugin.Configuration.shikimoriAppName = "anidoki-tests";
        _plugin.Configuration.ProviderApiAuth = [new ProviderApiAuth { Name = ApiName.Simkl, ClientId = "simkl-client" }];

        IHttpClientFactory http = null!;
        Helpers.MockHttpCalls(new List<Helpers.HttpCall> {
            new() {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("/ids"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
                    Anilist = EntryId,
                    MyAnimeList = EntryId,
                    Kitsu = EntryId,
                    AniDb = EntryId
                })
            }
        }, ref http);
        _http = http;
    }

    public static Anime Entry(int id, string title, int episodes, MyListStatus? status, params RelatedAnime[] related) => new() {
        Id = id,
        Title = title,
        AlternativeTitles = new AlternativeTitles { Synonyms = new List<string>() },
        NumEpisodes = episodes,
        MyListStatus = status!,
        RelatedAnime = related.ToList()
    };

    public static RelatedAnime Related(RelationType type, int id) => new() { RelationType = type, Anime = new Anime { Id = id } };

    public FakeTracker Tracker(ApiName provider) {
        if (!_trackers.TryGetValue(provider, out var tracker))
            _trackers[provider] = tracker = new FakeTracker();
        return tracker;
    }

    /// <summary>Gives every provider the same entries; searches return <paramref name="searchResult"/>.</summary>
    public void SeedAll(Anime searchResult, params Anime[] others) {
        foreach (var provider in AllProviders) {
            var tracker = Tracker(provider);
            tracker.Add(searchResult);
            foreach (var entry in others)
                tracker.Add(entry);
            tracker.SearchResults.Add(searchResult);
        }
    }

    public void ResetTrackers() => _trackers.Clear();

    public void ConfigureUser(Guid user, bool planToWatchOnly, bool rewatchCompleted, params ApiName[] providers) {
        _plugin.Configuration.UserConfig = [
            new UserConfig {
                UserId = user,
                LibraryToCheck = Array.Empty<string>(),
                PlanToWatchOnly = planToWatchOnly,
                RewatchCompleted = rewatchCompleted,
                UserApiAuth = providers.Select(provider => new UserApiAuth {
                    Name = provider,
                    AccessToken = "accessToken",
                    RefreshToken = "refreshToken"
                }).ToArray()
            }
        ];
    }

    public UpdateProviderStatus NewUpdater() => new(Library.Library.Object, new NullLoggerFactory(),
        new Mock<IHttpContextAccessor>().Object, new Mock<IServerApplicationHost>().Object, _http,
        new Mock<IApplicationPaths>().Object, new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object) {
        ApiCallHelpersFactory = (provider, _) => Tracker(provider)
    };

    /// <summary>Runs one sync and returns one line per provider: <c>Provider: writes | outcomes</c>.</summary>
    public async Task<string> Run(UpdateProviderStatus updater, BaseItem item, Guid user, SyncUpdateSource source, params ApiName[] providers) {
        var before = providers.ToDictionary(provider => provider, provider => Tracker(provider).Writes.Count);
        var collector = new PlaybackOutcomeCollector();
        await updater.UpdateInsideGate(item, user, true, collector, source);
        return string.Join("\n", providers.Select(provider => $"{provider}: {Describe(provider, before[provider], collector)}"));
    }

    /// <summary>Runs one sync for a single provider and returns <c>writes | outcomes</c>.</summary>
    public async Task<string> RunSingle(UpdateProviderStatus updater, BaseItem item, Guid user, SyncUpdateSource source, ApiName provider) {
        int before = Tracker(provider).Writes.Count;
        var collector = new PlaybackOutcomeCollector();
        await updater.UpdateInsideGate(item, user, true, collector, source);
        return Describe(provider, before, collector);
    }

    private string Describe(ApiName provider, int writesBefore, PlaybackOutcomeCollector collector) {
        var writes = Tracker(provider).Writes.Skip(writesBefore).Select(write => write.ToString()).ToList();
        var outcomes = collector.Outcomes.Where(outcome => outcome.Provider == provider).Select(Describe).ToList();
        return $"{(writes.Count == 0 ? "no writes" : string.Join(" + ", writes))} | {(outcomes.Count == 0 ? "no outcomes" : string.Join(", ", outcomes))}";
    }

    private static string Describe(ProviderWriteOutcome outcome) =>
        $"{outcome.Kind}:{outcome.Step}@{(string.IsNullOrEmpty(outcome.TargetId) ? "?" : outcome.TargetId)}";

    public void Dispose() => Directory.Delete(_directory, true);
}
