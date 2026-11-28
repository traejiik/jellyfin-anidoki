using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using jellyfin_anidoki.Settings;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Helpers;

public class SyncHelper {
    /// <summary>The movies and series the user can see, less those in an Off library (<see cref="LibraryFilter.WithoutOffLibraries"/>).</summary>
    public static IReadOnlyList<BaseItem> GetUsersJellyfinLibrary(Guid userId, IUserManager userManager, ILibraryManager libraryManager, ILogger logger) {
        var query = new InternalItemsQuery(userManager.GetUserById(userId)) {
            IncludeItemTypes = new[] {
                BaseItemKind.Movie,
                BaseItemKind.Series
            },
            IsVirtualItem = false
        };

        return LibraryFilter.WithoutOffLibraries(libraryManager, logger, libraryManager.GetItemList(query));
    }

    /// <summary>
    /// Get the preferred series provider ID. 
    /// </summary>
    public static (ArmClient.Source? source, int? providerId) GetSeriesProviderId(Series series) {
        if (series.ProviderIds.ContainsKey("AniList") && int.TryParse(series.ProviderIds["AniList"], out int providerId)) {
            return (ArmClient.Source.Anilist, providerId);
        } else if (series.ProviderIds.ContainsKey("AniDB") && int.TryParse(series.ProviderIds["AniDB"], out providerId)) {
            return (ArmClient.Source.Anidb, providerId);
        }

        return (null, null);
    }

    /// <summary>
    /// Get a list of seasons corresponding with their provider ID.
    /// </summary>
    /// <param name="convertedWatchList">List of metadata to match to seasons.</param>
    /// <param name="providerId">AniList provider ID.</param>
    /// <param name="series">Series to retrieve season of.</param>
    /// <param name="source">Metadata source.</param>
    /// <returns>List of seasons with their completed date and progress.</returns>
    public static async Task<List<(Season Season, Sync.SyncAnimeMetadata Found)>> GetJellyfinSeasons(List<Sync.SyncAnimeMetadata> convertedWatchList,
        int providerId,
        Series series,
        ArmClient.Source source,
        ILogger logger,
        ArmClient arm,
        AnimeListSource animeList) {
        List<(Season Season, Sync.SyncAnimeMetadata Found)> seasons = new List<(Season Season, Sync.SyncAnimeMetadata Found)>();

        List<AnimeListAnimeOfflineDatabaseCombo>? seasonIdsFromJellyfin = await GetSeasonIdsFromJellyfin(providerId,
            source,
            logger,
            arm,
            animeList);

        if (seasonIdsFromJellyfin != null) {
            foreach (AnimeListAnimeOfflineDatabaseCombo animeListAnime in seasonIdsFromJellyfin) {
                if (animeListAnime.AnimeListAnime != null && animeListAnime.AnimeListAnime.Anidbid != null) {
                    var found = convertedWatchList.FirstOrDefault(item => item?.ids?.AniDb != null && int.TryParse(animeListAnime?.AnimeListAnime?.Anidbid, out int convertedAniDbId) && item.ids.AniDb == convertedAniDbId);
                    if (found != null) {
                        var season = series.Children.Select(season => season as Season).FirstOrDefault(season => season?.IndexNumber == found.season);
                        if (season != null) {
                            seasons.Add((season, found));
                            logger.LogDebug("Matched {SeriesName} season {SeasonNumber} to AniDB ID {AniDbId}", series.Name, season.IndexNumber, animeListAnime.AnimeListAnime.Anidbid);
                        } else {
                            logger.LogDebug("{SeriesName} has no season {SeasonNumber} in Jellyfin for AniDB ID {AniDbId}", series.Name, found.season, animeListAnime.AnimeListAnime.Anidbid);
                        }
                    }
                } else {
                    logger.LogDebug("An anime list row for {SeriesName} has no AniDB ID", series.Name);
                }

            }
        }

        return seasons;
    }


    /// <summary>
    /// Get season IDs from Jellyfin library.
    /// </summary>
    /// <param name="providerId">ID of the provider.</param>
    /// <returns>List of season IDs.</returns>
    public static async Task<List<AnimeListAnimeOfflineDatabaseCombo>?> GetSeasonIdsFromJellyfin(int providerId, ArmClient.Source source,
        ILogger logger,
        ArmClient arm,
        AnimeListSource animeList) {
        var id = await arm.GetIds(providerId, source);
        if (id is { AniDb: { } }) {
            AnimeListHelpers.AnimeListXml? animeListXml = await animeList.Read();
            var seasons = AnimeListHelpers.ListAllSeasonOfAniDbSeries(id.AniDb.Value, animeListXml);
            List<AnimeListHelpers.AnimeListAnime> seasonsList;
            if (seasons != null) {
                seasonsList = seasons.ToList();
            } else {
                logger.LogDebug("The anime list has no seasons for AniDB ID {AniDbId}", id.AniDb.Value);
                return null;
            }

            if (seasonsList.Count() > 1) {
                return seasonsList.Select(animeListAnime => new AnimeListAnimeOfflineDatabaseCombo { AnimeListAnime = animeListAnime }).ToList();
            } else {
                var seasonMetadataCombo = new List<AnimeListAnimeOfflineDatabaseCombo> {
                    new() {
                        AnimeListAnime = seasonsList.First(),
                        OfflineDatabaseResponse = id
                    }
                };
                return seasonMetadataCombo;
            }
        }

        return null;
    }

    private const string IgnoreTag = "jas-ignore";

    /// <summary>
    /// Whether the item itself carries the ignore tag. Does not look at parents.
    /// </summary>
    public static bool HasIgnoreTag(BaseItem? item) => item?.Tags.Contains(IgnoreTag, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether the item, its parent season or its series carries the ignore tag.
    /// </summary>
    public static bool MediaShouldBeIgnored(BaseItem? item) {
        if (item == null)
            return false;
        return HasIgnoreTag(item) || HasIgnoreTag(item.GetParent()) || (item is Episode episode && HasIgnoreTag(episode.Series));
    }

    public class AnimeListAnimeOfflineDatabaseCombo {
        public required AnimeListHelpers.AnimeListAnime AnimeListAnime { get; set; }
        public ArmClient.OfflineDatabaseResponse? OfflineDatabaseResponse { get; set; }
    }

    /// <summary>Which tracker list entries a sync reads. Its name must stay unique among the types the plugin's routes
    /// expose, or Jellyfin cannot build its API docs (the MAL list status is already called Status).</summary>
    public enum StatusFilter {
        Completed,
        Watching,
        Both
    }
}
