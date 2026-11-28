using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using jellyfin_anidoki.Trackers.Mal;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using jellyfin_anidoki.Helpers;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

public class AnimeListHelperTests {
    [Test]
    public void GetCorrectAniDbSeasonCount() {
        int aniDbId = 1;
        string tvdbId = "abc123";
        AnimeListHelpers.AnimeListXml animeListXml = new AnimeListHelpers.AnimeListXml {
            Anime = new List<AnimeListHelpers.AnimeListAnime> {
                new() {
                    Anidbid = aniDbId.ToString(),
                    Tvdbid = tvdbId
                },
                new() {
                    Anidbid = aniDbId.ToString(),
                    Tvdbid = tvdbId
                },
                new() {
                    Anidbid = aniDbId.ToString(),
                    Tvdbid = String.Empty
                }
            }
        };
        var returned = AnimeListHelpers.ListAllSeasonOfAniDbSeries(aniDbId, animeListXml);
        Assert.That(returned, Is.Not.Null);
        Assert.IsTrue(returned!.Count() == 2);
    }

    [Test]
    public void GetCorrectAniDbSeason() {
        int aniDbId = 1;
        string tvdbId = "abc123";
        AnimeListHelpers.AnimeListXml animeListXml = new AnimeListHelpers.AnimeListXml {
            Anime = new List<AnimeListHelpers.AnimeListAnime> {
                new() {
                    Anidbid = aniDbId.ToString(),
                    Tvdbid = tvdbId
                },
                new() {
                    Anidbid = aniDbId.ToString(),
                    Tvdbid = tvdbId
                },
                new() {
                    Anidbid = aniDbId.ToString(),
                    Tvdbid = String.Empty
                }
            }
        };
        var returned = AnimeListHelpers.GetAniDbSeason(aniDbId, animeListXml);
        Assert.That(returned, Is.Not.Null);
        Assert.IsTrue(returned!.Anidbid == aniDbId.ToString());
    }

    /// <summary>An anime list without rows finds nothing, as a missing list does, instead of throwing.</summary>
    [Test]
    public async Task AnAnimeListWithoutRowsFindsNothing() {
        var empty = new AnimeListHelpers.AnimeListXml();
        var movie = new Movie { Name = "Film", ProviderIds = new Dictionary<string, string> { ["AniDB"] = "1" } };

        Assert.That(AnimeListHelpers.GetAniDbSeason(1, empty), Is.Null);
        Assert.That(AnimeListHelpers.ListAllSeasonOfAniDbSeries(1, empty), Is.Null);
        Assert.That(await AnimeListHelpers.GetAniDbId(NullLogger.Instance, movie, 1, 1, empty), Is.EqualTo(((int?)null, (int?)null)));
    }
}
