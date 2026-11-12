using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using jellyfin_anidoki;
using jellyfin_anidoki.Api;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki.Trackers;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace jellyfin_anidoki_unit_tests.Characterization;

/// <summary>A tracker list kept in memory. AniList and Kitsu ask for the user first; any non-zero ID will do.</summary>
internal sealed class FakeListSource : ITrackerListSource {
    public Dictionary<Status, List<Anime>> Lists { get; } = new() { [Status.Completed] = [], [Status.Watching] = [] };

    /// <summary>Answer null for every list, as a failed request does.</summary>
    public bool NoAnswer { get; set; }

    public Task<MalApiCalls.User?> GetUser() => Task.FromResult<MalApiCalls.User?>(new MalApiCalls.User { Id = 1 });

    public Task<List<Anime>?> GetAnimeList(Status status, int? userId = null) =>
        Task.FromResult(NoAnswer ? null : Lists.GetValueOrDefault(status)?.ToList());
}

/// <summary>
/// Everything inbound sync reads, in memory: the tracker list, ARM's ID lookups, the anime-list XML, the user's series
/// tree and their user data. Saves are recorded as text. One harness is one plugin instance; dispose it after each scenario.
/// </summary>
internal sealed class InboundHarness : IDisposable {
    private readonly string _directory;
    private readonly Dictionary<(string Source, int Id), int> _aniDb = new();
    private readonly List<string> _animeList = new();
    private readonly List<BaseItem> _series = new();
    private readonly Dictionary<Guid, UserItemData> _data = new();
    private readonly IHttpClientFactory _http;

    public Guid UserId { get; } = Guid.NewGuid();
    public User User { get; }
    public Mock<ILibraryManager> Library { get; } = new();
    public Mock<IUserManager> Users { get; } = new();
    public Mock<IUserDataManager> UserData { get; }
    public Mock<IAsyncDelayer> Delayer { get; } = new();
    public FakeListSource Tracker { get; } = new();
    public List<string> Saves { get; } = new();

    /// <param name="strictUserData">True for a preview: reading user data is allowed, saving it throws.</param>
    public InboundHarness(bool strictUserData = false) {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-inbound-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        BaseItem.LibraryManager = Library.Object;
        User = new User("test", "authentication", "password-reset") { Id = UserId };
        Users.Setup(manager => manager.GetUserById(UserId)).Returns(User);
        Library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => _series.ToList());

        UserData = new Mock<IUserDataManager>(strictUserData ? MockBehavior.Strict : MockBehavior.Loose);
        UserData.Setup(manager => manager.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>())).Returns((User _, BaseItem item) => DataFor(item));
        if (!strictUserData) {
            UserData.Setup(manager => manager.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()))
                .Callback((User _, BaseItem item, UserItemData data, UserDataSaveReason _, CancellationToken _) =>
                    Saves.Add($"{Describe(item)} {(data.Played ? "played" : "unplayed")} {Date(data.LastPlayedDate)}"));
        }

        var http = new Mock<IHttpClientFactory>();
        http.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new ArmHandler(_aniDb)));
        _http = http.Object;

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(_directory);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(_directory);
        paths.SetupGet(p => p.DataPath).Returns(_directory);
        paths.SetupGet(p => p.CachePath).Returns(_directory);
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration {
            animeListSaveLocation = _directory,
            UserConfig = [new UserConfig {
                UserId = UserId,
                LibraryToCheck = [],
                UserApiAuth = [new UserApiAuth { Name = ApiName.AniList, AccessToken = "accessToken", RefreshToken = "refreshToken" }]
            }]
        });
        _ = new Plugin(paths.Object, new Mock<IServerConfigurationManager>().Object, xml.Object, NullLogger<Plugin>.Instance);
    }

    /// <summary>An anime-list row: this AniDB entry is TVDB series <paramref name="tvdbId"/>, season <paramref name="season"/>.</summary>
    public void Map(int aniDbId, int tvdbId, string season, int? offset = null) =>
        _animeList.Add($"<anime anidbid=\"{aniDbId}\" tvdbid=\"{tvdbId}\" defaulttvdbseason=\"{season}\"" +
            (offset is { } value ? $" episodeoffset=\"{value}\"" : "") + " />");

    /// <summary>What ARM answers for <paramref name="source"/> (anilist, anidb, myanimelist, kitsu) and this ID. Unknown IDs answer null.</summary>
    public void Ids(string source, int id, int aniDbId) => _aniDb[(source, id)] = aniDbId;

    public Anime Entry(int id, string title, Status status, int progress, string? finishDate = null) {
        var anime = new Anime { Id = id, Title = title, MyListStatus = new MyListStatus { Status = status, NumEpisodesWatched = progress, FinishDate = finishDate } };
        Tracker.Lists[status].Add(anime);
        return anime;
    }

    /// <summary>A series known by its AniList ID, with numbered seasons of 1..n episodes.</summary>
    public Series Series(string name, int aniListId, params (int Number, int Episodes)[] seasons) {
        var series = new Series { Id = Guid.NewGuid(), Name = name, ProviderIds = new Dictionary<string, string> { ["AniList"] = aniListId.ToString(CultureInfo.InvariantCulture) } };
        Register(series);
        var children = new List<BaseItem>();
        foreach (var (number, count) in seasons) {
            var season = new Season { Id = Guid.NewGuid(), Name = $"Season {number}", IndexNumber = number, ParentId = series.Id, SeriesId = series.Id, SeriesName = name };
            Register(season);
            var episodes = new List<BaseItem>();
            for (int index = 1; index <= count; index++) {
                var episode = new Episode {
                    Id = Guid.NewGuid(),
                    Name = $"Episode {index}",
                    IndexNumber = index,
                    ParentIndexNumber = number,
                    ParentId = season.Id,
                    SeasonId = season.Id,
                    SeriesId = series.Id,
                    SeriesName = name
                };
                Register(episode);
                episodes.Add(episode);
            }
            season.Children = episodes;
            children.Add(season);
        }
        series.Children = children;
        _series.Add(series);
        return series;
    }

    public static Season SeasonOf(Series series, int number) => series.Children.OfType<Season>().Single(season => season.IndexNumber == number);

    public static Episode EpisodeOf(Series series, int season, int episode) =>
        SeasonOf(series, season).Children.OfType<Episode>().Single(item => item.IndexNumber == episode);

    public static void Ignore(BaseItem item) => item.Tags = ["jas-ignore"];

    public void Played(BaseItem item) => DataFor(item).Played = true;

    public Sync NewSync(SyncHelper.Status status = SyncHelper.Status.Both, ApiName provider = ApiName.AniList) {
        File.WriteAllText(Path.Combine(_directory, "anime-list-full.xml"), $"<anime-list>{string.Concat(_animeList)}</anime-list>");
        return new Sync(_http, NullLoggerFactory.Instance, new Mock<IServerApplicationHost>().Object, new HttpContextAccessor(), Users.Object,
            Library.Object, new Mock<IApplicationPaths>().Object, UserData.Object, new MemoryCache(new MemoryCacheOptions()), Delayer.Object,
            provider, status) {
            ListSourceOverride = (_, _) => Tracker
        };
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private UserItemData DataFor(BaseItem item) {
        if (!_data.TryGetValue(item.Id, out UserItemData? data)) {
            data = new UserItemData { Key = item.Id.ToString("N") };
            _data[item.Id] = data;
        }
        return data;
    }

    private void Register(BaseItem item) {
        Library.Setup(manager => manager.GetItemById(item.Id)).Returns(item);
        Library.Setup(manager => manager.GetItemById<BaseItem>(item.Id)).Returns(item);
        if (item is Series series)
            Library.Setup(manager => manager.GetItemById<Series>(series.Id)).Returns(series);
        if (item is Season season)
            Library.Setup(manager => manager.GetItemById<Season>(season.Id)).Returns(season);
    }

    private static string Describe(BaseItem item) => item switch {
        Episode episode => $"{episode.SeriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}",
        Season season => $"{season.SeriesName} season {season.IndexNumber}",
        _ => item.Name
    };

    /// <summary>The fallback for a missing finish date is the time of the run, so it prints as <c>now</c>.</summary>
    private static string Date(DateTime? date) => date switch {
        null => "-",
        { } value when Math.Abs((value - DateTime.UtcNow).TotalMinutes) < 5 => "now",
        { } value => value.ToString("s", CultureInfo.InvariantCulture)
    };

    private sealed class ArmHandler(Dictionary<(string Source, int Id), int> aniDb) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            int id = int.Parse(query["id"]!, CultureInfo.InvariantCulture);
            string body = aniDb.TryGetValue((query["source"]!, id), out int found)
                ? JsonSerializer.Serialize(new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse { AniDb = found })
                : "null";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
