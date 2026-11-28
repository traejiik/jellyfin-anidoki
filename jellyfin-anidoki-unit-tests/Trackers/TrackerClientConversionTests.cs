using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using jellyfin_anidoki.AnimeIds;
using jellyfin_anidoki.Auth;
using jellyfin_anidoki.Common;
using jellyfin_anidoki.Settings;
using jellyfin_anidoki.Trackers;
using jellyfin_anidoki.Trackers.AniList;
using jellyfin_anidoki.Trackers.Annict;
using jellyfin_anidoki.Trackers.Kitsu;
using jellyfin_anidoki.Trackers.Mal;
using jellyfin_anidoki.Trackers.Shikimori;
using jellyfin_anidoki.Trackers.Simkl;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using ILoggerFactory = Microsoft.Extensions.Logging.ILoggerFactory;

namespace jellyfin_anidoki_unit_tests.Trackers;

public class TrackerClientConversionTests {
    private int nextKitsuId;

    [SetUp]
    public void Setup() {
        nextKitsuId = 0;
    }

    [Test]
    public void AniListSearchAnimeConvertedListTest() {
        List<AniListSearch.Media> mediaList = new List<AniListSearch.Media>();
        for (int i = 0; i < 10; i++) {
            mediaList.Add(GetAniListMedia(true));
        }

        List<Anime> animeList = AniListTrackerClient.ConvertSearchResults(mediaList, true);
        Assert.AreEqual(10, animeList.Count);
        // Assert that the anime list contains the expected anime objects
        for (int i = 0; i < animeList.Count; i++) {
            Assert.AreEqual(mediaList[i].Id, animeList[i].Id);
            Assert.AreEqual(mediaList[i].Episodes, animeList[i].NumEpisodes);
            Assert.AreEqual(mediaList[i].Title!.English, animeList[i].Title);
            Assert.AreEqual(mediaList[i].Title!.English, animeList[i].AlternativeTitles!.En);
            Assert.AreEqual(mediaList[i].Title!.Native, animeList[i].AlternativeTitles!.Ja);
            Assert.AreEqual(new List<string> { mediaList[i].Title!.Romaji!, mediaList[i].Title!.UserPreferred!, "Synonym1", "Synonym2" }, animeList[i].AlternativeTitles!.Synonyms);
        }
    }

    [Test]
    public void KitsuSearchAnimeConvertedListTest() {
        List<KitsuSearch.KitsuAnime> mediaList = new List<KitsuSearch.KitsuAnime>();
        for (int i = 0; i < 10; i++) {
            mediaList.Add(GetKitsuAnime(true));
        }

        List<Anime> animeList = KitsuTrackerClient.ConvertSearchResults(mediaList);
        Assert.AreEqual(10, animeList.Count);
        // Assert that the anime list contains the expected anime objects
        for (int i = 0; i < animeList.Count; i++) {
            Assert.AreEqual(mediaList[i].Id, animeList[i].Id);
            Assert.AreEqual(mediaList[i].Attributes!.EpisodeCount, animeList[i].NumEpisodes);
            Assert.AreEqual(mediaList[i].Attributes!.Titles!.English, animeList[i].Title);
            Assert.AreEqual(mediaList[i].Attributes!.Titles!.English, animeList[i].AlternativeTitles!.En);
            Assert.AreEqual(mediaList[i].Attributes!.Titles!.Japanese, animeList[i].AlternativeTitles!.Ja);
            // The fixture has no slug, so it is left out rather than kept as a null entry.
            Assert.AreEqual(new List<string> { "Title", "Synonym1", "Synonym2" }, animeList[i].AlternativeTitles!.Synonyms);
        }
    }

    [Test]
    public void AnnictSearchAnimeConvertedListTest() {
        List<AnnictSearch.AnnictAnime> mediaList = new List<AnnictSearch.AnnictAnime>();
        for (int i = 0; i < 10; i++) {
            mediaList.Add(GetAnnictAnime());
        }

        List<Anime> animeList = AnnictTrackerClient.ConvertSearchResults(mediaList);
        Assert.AreEqual(10, animeList.Count);
        // Assert that the anime list contains the expected anime objects
        for (int i = 0; i < animeList.Count; i++) {
            Assert.AreEqual(mediaList[i].Id, animeList[i].AlternativeId);
            Assert.AreEqual(mediaList[i].TitleEn, animeList[i].Title);
            Assert.AreEqual(mediaList[i].MalAnimeId, animeList[i].Id.ToString());
            Assert.AreEqual(mediaList[i].NumberOfEpisodes, animeList[i].NumEpisodes);
        }
    }

    [Test]
    public void ShikimoriSearchAnimeConvertedListTest() {
        List<ShikimoriAnime> mediaList = new List<ShikimoriAnime>();
        for (int i = 0; i < 10; i++) {
            mediaList.Add(GetShikimoriAnime(true));
        }

        List<Anime> animeList = ShikimoriTrackerClient.ConvertSearchResults(mediaList, true);
        Assert.AreEqual(10, animeList.Count);
        // Assert that the anime list contains the expected anime objects
        for (int i = 0; i < animeList.Count; i++) {
            Assert.AreEqual(mediaList[i].MalId, animeList[i].Id.ToString());
            Assert.AreEqual(mediaList[i].Episodes, animeList[i].NumEpisodes);
            Assert.AreEqual(mediaList[i].Name, animeList[i].Title);
            Assert.AreEqual(mediaList[i].Synonyms!.Append(mediaList[i].Russian).ToList(), animeList[i].AlternativeTitles!.Synonyms);
            Assert.AreEqual(mediaList[i].English, animeList[i].AlternativeTitles!.En);
            Assert.AreEqual(mediaList[i].Japanese, animeList[i].AlternativeTitles!.Ja);
            Assert.IsNotNull(mediaList[i].UserRate);
            Assert.AreEqual(mediaList[i].UserRate!.Episodes, animeList[i].MyListStatus!.NumEpisodesWatched);
            Assert.AreEqual(mediaList[i].UserRate!.Status == ShikimoriUserRate.StatusEnum.rewatching, animeList[i].MyListStatus!.IsRewatching);
            Assert.AreEqual(mediaList[i].UserRate!.Rewatches, animeList[i].MyListStatus!.RewatchCount);
            Assert.IsNotNull(mediaList[i].Related);
            Assert.AreEqual(mediaList[i].Related!.Count, animeList[i].RelatedAnime!.Count);
        }
    }

    [Test]
    public void KitsuConfirmRelatedAnimeExists() {
        List<KitsuSearch.KitsuAnime> mediaList = new List<KitsuSearch.KitsuAnime>();
        for (int i = 0; i < 10; i++) {
            mediaList.Add(GetKitsuAnime(true));
        }

        List<Anime> animeList = KitsuTrackerClient.ConvertSearchResults(mediaList);
        Assert.AreEqual(10, animeList.Count);
        // Assert that the anime list contains the expected anime objects
        foreach (var anime in animeList) {
            var matchingAnime = mediaList.Single(item => item.Id == anime.Id);
            Assert.AreEqual(matchingAnime.RelatedAnime!.Count, anime.RelatedAnime!.Count);
            Assert.IsNotEmpty(anime.RelatedAnime);
            foreach (RelatedAnime relatedAnime in anime.RelatedAnime) {
                var matchingRelated = matchingAnime.RelatedAnime!.Single(item => item.Id == relatedAnime.Anime!.Id);
                switch (matchingRelated.RelationType) {
                    case KitsuMediaRelationship.RelationType.sequel:
                        Assert.AreEqual(RelationType.Sequel, relatedAnime.RelationType, $"Kitsu relation {matchingRelated.RelationType}");
                        break;
                    case KitsuMediaRelationship.RelationType.prequel:
                        Assert.AreEqual(RelationType.Prequel, relatedAnime.RelationType, $"Kitsu relation {matchingRelated.RelationType}");
                        break;
                    case KitsuMediaRelationship.RelationType.side_story:
                    case KitsuMediaRelationship.RelationType.full_story:
                    case KitsuMediaRelationship.RelationType.parent_story:
                        Assert.AreEqual(RelationType.Side_Story, relatedAnime.RelationType, $"Kitsu relation {matchingRelated.RelationType}");
                        break;
                    case KitsuMediaRelationship.RelationType.alternative_setting:
                        Assert.AreEqual(RelationType.Alternative_Setting, relatedAnime.RelationType, $"Kitsu relation {matchingRelated.RelationType}");
                        break;
                    case KitsuMediaRelationship.RelationType.alternative_version:
                        Assert.AreEqual(RelationType.Alternative_Version, relatedAnime.RelationType, $"Kitsu relation {matchingRelated.RelationType}");
                        break;
                    case KitsuMediaRelationship.RelationType.spinoff:
                    case KitsuMediaRelationship.RelationType.adaptation:
                        Assert.AreEqual(RelationType.Spin_Off, relatedAnime.RelationType, $"Kitsu relation {matchingRelated.RelationType}");
                        break;
                    default:
                        Assert.AreEqual(RelationType.Other, relatedAnime.RelationType, $"Kitsu relation {matchingRelated.RelationType}");
                        break;
                }

                Assert.AreEqual(matchingRelated.Id, relatedAnime.Anime!.Id);
                Assert.AreEqual(matchingRelated.Attributes!.EpisodeCount, relatedAnime.Anime!.NumEpisodes);
                Assert.AreEqual(matchingRelated.Attributes!.Titles!.English, relatedAnime.Anime!.Title);
                Assert.AreEqual(matchingRelated.Attributes!.Titles!.English, relatedAnime.Anime!.AlternativeTitles!.En);
                Assert.AreEqual(matchingRelated.Attributes.Titles.Japanese, relatedAnime.Anime.AlternativeTitles.Ja);
                Assert.AreEqual(new List<string> { "Title", "Synonym1", "Synonym2" }, relatedAnime.Anime!.AlternativeTitles!.Synonyms);
            }
        }
    }

    [TestCase(1, true, 1, KitsuUpdate.Status.completed, 1, true, 1, Status.Completed)]
    [TestCase(null, null, null, KitsuUpdate.Status.dropped, 0, false, 0, Status.Dropped)]
    [TestCase(10, false, 10, KitsuUpdate.Status.on_hold, 10, false, 10, Status.On_hold)]
    [TestCase(5, null, 5, KitsuUpdate.Status.current, 5, false, 5, Status.Watching)]
    [TestCase(5, true, null, KitsuUpdate.Status.planned, 5, true, 0, Status.Plan_to_watch)]
    public async Task KitsuGetConvertedUserListTest(int progress, bool reconsuming, int reconsumeCount, KitsuUpdate.Status status, int expectedProgress, bool expectedReconsuming, int expectedReconsumeCount, Status expectedStatus) {
        IHttpClientFactory httpClientFactory = null!;
        ILoggerFactory loggerFactory = new NullLoggerFactory();
        Mock<IServerApplicationHost> serverApplicationHost = new Mock<IServerApplicationHost>();
        Mock<IHttpContextAccessor> httpContextAccessor = new Mock<IHttpContextAccessor>();
        MemoryCache memoryCache = new MemoryCache(new MemoryCacheOptions());
        Mock<IAsyncDelayer> mockDelayer = new Mock<IAsyncDelayer>();
        int animeId = 1;
        Helpers.MockHttpCalls(new List<Helpers.HttpCall> {
            new () {
                RequestMethod = HttpMethod.Get,
                RequestUrlMatch = url => url.EndsWith("library-entries"),
                ResponseCode = HttpStatusCode.OK,
                ResponseContent = JsonSerializer.Serialize(new KitsuUpdate.KitsuLibraryEntryListRoot {
                    Data = new List<KitsuUpdate.KitsuLibraryEntry> {
                        new () {
                            Id = animeId,
                            Attributes = new KitsuUpdate.Attributes {
                                Progress = progress,
                                Reconsuming = reconsuming,
                                ReconsumeCount = reconsumeCount,
                                Status = status
                            }
                        }
                    }
                })
            }
        }, ref httpClientFactory);

        KitsuApiCalls kitsuApiCalls = new KitsuApiCalls(httpClientFactory, loggerFactory, serverApplicationHost.Object, httpContextAccessor.Object, memoryCache, mockDelayer.Object, new UserConfig {
            UserApiAuth = new[] {
                new UserApiAuth {
                    AccessToken = "accessToken",
                    Name = ApiName.Kitsu,
                    RefreshToken = "refreshToken"
                }
            },
            KeyPairs = new List<KeyPairs> { new() { Key = "KitsuUserId", Value = "1" } }
        });

        var client = new KitsuTrackerClient(kitsuApiCalls);
        MyListStatus convertedResult = await client.GetConvertedUserList(animeId);

        Assert.IsTrue(convertedResult.NumEpisodesWatched == expectedProgress);
        Assert.IsTrue(convertedResult.IsRewatching == expectedReconsuming);
        Assert.IsTrue(convertedResult.RewatchCount == expectedReconsumeCount);
        Assert.IsTrue(convertedResult.Status == expectedStatus);
    }

    [TestCase(ApiName.Kitsu)]
    [TestCase(ApiName.Shikimori)]
    public async Task MutationReceiptDoesNotInventUnreturnedRewatchFields(ApiName provider) {
        IHttpClientFactory factory = null!;
        Helpers.MockHttpCalls(new List<Helpers.HttpCall> {
            new() { RequestMethod = HttpMethod.Get, ResponseCode = HttpStatusCode.OK, ResponseContent = "{\"data\":[]}" },
            new() { RequestMethod = HttpMethod.Post, ResponseCode = HttpStatusCode.OK, ResponseContent = provider == ApiName.Kitsu
                ? "{\"data\":{\"id\":\"9\",\"type\":\"libraryEntries\",\"attributes\":{\"progress\":5,\"status\":\"current\"}}}"
                : "{\"id\":9,\"target_id\":1,\"target_type\":\"Anime\",\"episodes\":5,\"status\":\"watching\"}" }
        }, ref factory);
        var config = new UserConfig {
            UserApiAuth = [new UserApiAuth { Name = provider, AccessToken = "test" }],
            KeyPairs = [new KeyPairs { Key = provider + "UserId", Value = "1" }]
        };
        var loggers = new NullLoggerFactory();
        var host = new Mock<IServerApplicationHost>().Object;
        var context = new Mock<IHttpContextAccessor>().Object;
        var cache = new MemoryCache(new MemoryCacheOptions());
        var delay = new Mock<IAsyncDelayer>().Object;
        ITrackerClient helpers = provider == ApiName.Kitsu
            ? new KitsuTrackerClient(new KitsuApiCalls(factory, loggers, host, context, cache, delay, config))
            : new ShikimoriTrackerClient(new ShikimoriApiCalls(factory, loggers, host, context, cache, delay,
                new Dictionary<string, string>(), config));
        var result = await helpers.UpdateAnime(1, 5, Status.Watching, isRewatching: true,
            numberOfTimesRewatched: 2, alternativeId: "1");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.AcknowledgedProgress, Is.EqualTo(5));
        Assert.That(result.AcknowledgedStatus, Is.EqualTo(Status.Watching));
        Assert.That(result.AcknowledgedRewatching, Is.Null);
        Assert.That(result.AcknowledgedRewatchCount, Is.Null);
    }

    private ShikimoriAnime GetShikimoriAnime(bool createRelations) {
        Random random = new Random();

        List<ShikimoriRelated> relatedAnime = new List<ShikimoriRelated>();
        if (createRelations) {
            for (int i = 0; i < random.Next(0, 5); i++) {
                relatedAnime.Add(new ShikimoriRelated {
                    Anime = GetShikimoriAnime(false),
                    Relation = ((ShikimoriRelation)random.Next(0, 4)).ToString(),
                });
            }
        }

        return new ShikimoriAnime {
            Id = random.Next(1, 100).ToString(),
            MalId = random.Next(1, 100).ToString(),
            Episodes = random.Next(1, 100),
            IsCensored = random.Next(0, 1) == 0,
            UserRate = new ShikimoriUserRate {
                Status = (ShikimoriUserRate.StatusEnum)random.Next(0, 6),
                Episodes = random.Next(0, 100),
                Rewatches = random.Next(0, 10),
            },
            Related = relatedAnime,
            Name = "Title",
            Synonyms = new List<string> { "Synonym1", "Synonym2" },
            English = "Title",
            Russian = "Title",
            Japanese = "Title"
        };
    }

    private AniListSearch.Media GetAniListMedia(bool createRelations) {
        Random random = new Random();
        AniListSearch.MediaConnection mediaConnection = new AniListSearch.MediaConnection();
        if (createRelations) {
            mediaConnection.Media = new List<AniListSearch.MediaEdge>();
            for (int i = 0; i < random.Next(0, 5); i++) {
                mediaConnection.Media.Add(new AniListSearch.MediaEdge {
                    Media = GetAniListMedia(false),
                    RelationType = (AniListSearch.MediaRelation)random.Next(0, 12)
                });
            }
        }

        return new AniListSearch.Media {
            Id = random.Next(1, 100),
            Episodes = random.Next(1, 100),
            IsAdult = random.Next(0, 1) == 0,
            MediaListEntry = new AniListSearch.MediaListEntry {
                CompletedAt = new AniListSearch.FuzzyDate {
                    Day = random.Next(1, 31),
                    Month = random.Next(1, 12),
                    Year = random.Next(1970, 2024)
                },
                MediaListStatus = (AniListSearch.MediaListStatus)random.Next(0, 5),
                Progress = random.Next(0, 100),
                RepeatCount = random.Next(0, 10),
                StartedAt = new AniListSearch.FuzzyDate {
                    Day = random.Next(1, 31),
                    Month = random.Next(1, 12),
                    Year = random.Next(1970, 2024)
                }
            },
            Relations = mediaConnection,
            Title = new AniListSearch.Title { Romaji = "Title", English = "Title", Native = "Title", UserPreferred = "Title" },
            Synonyms = new List<string> { "Synonym1", "Synonym2" },
            Status = (AniListSearch.AiringStatus)random.Next(0, 4),
            SiteUrl = "https://example.com"
        };
    }

    private KitsuSearch.KitsuAnime GetKitsuAnime(bool createRelations) {
        KitsuSearch.MediaRelationships mediaRelationships = new KitsuSearch.MediaRelationships();
        if (createRelations) {
            mediaRelationships.Data = new List<KitsuSearch.KitsuAnime>();
            foreach (var relationType in Enum.GetValues<KitsuMediaRelationship.RelationType>()) {
                KitsuSearch.KitsuAnime anime = GetKitsuAnime(false);
                anime.RelationType = relationType;
                mediaRelationships.Data.Add(anime);
            }
        }

        KitsuSearch.Relationships relationships = new KitsuSearch.Relationships {
            MediaRelationships = mediaRelationships
        };
        return new KitsuSearch.KitsuAnime {
            Id = ++nextKitsuId,
            Attributes = new KitsuSearch.Attributes {
                EpisodeCount = 24,
                CanonicalTitle = "Title",
                Titles = new KitsuSearch.Titles { English = "Title", EnJp = "Title", Japanese = "Title" },
                AbbreviatedTitles = new List<string> { "Synonym1", "Synonym2" },
            },
            Relationships = relationships,
            RelatedAnime = mediaRelationships.Data
        };
    }

    private AnnictSearch.AnnictAnime GetAnnictAnime() {
        Random random = new Random();

        return new AnnictSearch.AnnictAnime {
            Id = random.Next(1, 100).ToString(),
            TitleEn = "Title",
            MalAnimeId = random.Next(1, 10000).ToString(),
            ViewerStatusState = (AnnictSearch.AnnictMediaStatus)random.Next(0, 6),
            NumberOfEpisodes = random.Next(1, 100),
        };
    }

    [TestCase(Status.Completed)]
    [TestCase(Status.Watching)]
    public void AniListListEntriesCarryTheirTitleAndTheStatusTheListWasReadFor(Status status) {
        const string json = """
            [
              { "media": { "id": 227, "siteUrl": "https://anilist.co/anime/227", "title": { "english": "FLCL", "romaji": "Furi Kuri" } },
                "completedAt": { "year": 2026, "month": 11, "day": 3 }, "progress": 6 },
              { "media": { "id": 154587, "siteUrl": "https://anilist.co/anime/154587", "title": { "english": null, "romaji": "Sousou no Frieren" } },
                "completedAt": { "year": null, "month": null, "day": null }, "progress": 14 }
            ]
            """;
        var entries = JsonSerializer.Deserialize<List<AniListMediaList.Entries>>(json);

        List<Anime> converted = AniListTrackerClient.ConvertList(entries, status);

        Assert.That(converted.Select(anime => (anime.Id, anime.Title, anime.MyListStatus!.Status, anime.MyListStatus!.NumEpisodesWatched)),
            Is.EqualTo(new[] { (227, "FLCL", status, 6), (154587, "Sousou no Frieren", status, 14) }));
    }

    [Test]
    public void AniListResultsMissingTitlesOrAPageAddressAreReadNotThrown() {
        var search = JsonSerializer.Deserialize<List<AniListSearch.Media>>("""[{ "id": 1, "title": null, "synonyms": null }]""")!;
        var list = JsonSerializer.Deserialize<List<AniListMediaList.Entries>>("""[{ "media": null, "progress": 2 }, { "media": { "id": 2 }, "progress": 3 }]""");

        List<Anime> searched = AniListTrackerClient.ConvertSearchResults(search, true);
        List<Anime> listed = AniListTrackerClient.ConvertList(list, Status.Watching);

        Assert.That(searched.Select(anime => (anime.Id, anime.Title, anime.AlternativeTitles?.Synonyms?.Count)),
            Is.EqualTo(new[] { (1, (string?)null, (int?)0) }));
        Assert.That(listed, Is.Empty);
    }

    [Test]
    public void KitsuListEntriesTakeTheirTitleFromTheIncludedAnime() {
        const string json = """
            {
              "data": [
                { "id": "1", "type": "libraryEntries", "attributes": { "status": "completed", "progress": 28 },
                  "relationships": { "anime": { "data": { "type": "anime", "id": "500" } } } },
                { "id": "2", "type": "libraryEntries", "attributes": { "status": "completed", "progress": 12 },
                  "relationships": { "anime": { "data": { "type": "anime", "id": "501" } } } },
                { "id": "3", "type": "libraryEntries", "attributes": { "status": "completed", "progress": 3 },
                  "relationships": { "anime": { "data": { "type": "anime", "id": "502" } } } }
              ],
              "included": [
                { "id": "500", "type": "anime", "attributes": { "canonicalTitle": "Sousou no Frieren", "titles": { "en": "Frieren: Beyond Journey's End" } } },
                { "id": "501", "type": "anime", "attributes": { "canonicalTitle": "Dungeon Meshi", "titles": { "en": null } } }
              ]
            }
            """;
        var root = JsonSerializer.Deserialize<KitsuUpdate.KitsuLibraryEntryListRoot>(json);

        List<Anime> converted = KitsuTrackerClient.ConvertList(root!, Status.Completed);

        Assert.That(converted.Select(anime => (anime.Id, anime.Title, anime.MyListStatus!.Status, anime.MyListStatus!.NumEpisodesWatched)),
            Is.EqualTo(new[] {
                (500, "Frieren: Beyond Journey's End", Status.Completed, 28), (501, "Dungeon Meshi", Status.Completed, 12),
                (502, (string)null!, Status.Completed, 3)
            }));
    }

    /// <summary>An AniList anime without a title, or with a relation that has no media, still converts with its list status.</summary>
    [Test]
    public void AnAniListAnimeWithoutTitlesOrRelationMediaConverts() {
        var media = new AniListSearch.Media {
            Id = 7,
            MediaListEntry = new AniListSearch.MediaListEntry { Progress = 3, MediaListStatus = AniListSearch.MediaListStatus.Current },
            Relations = new AniListSearch.MediaConnection { Media = [new AniListSearch.MediaEdge { RelationType = AniListSearch.MediaRelation.Sequel }] }
        };

        Anime anime = ClassConversions.ConvertAniListAnime(media);

        Assert.That(anime.Id, Is.EqualTo(7));
        Assert.That(anime.Title, Is.Null);
        Assert.That(anime.MyListStatus!.NumEpisodesWatched, Is.EqualTo(3));
        Assert.That(anime.MyListStatus.Status, Is.EqualTo(Status.Watching));
        Assert.That(anime.RelatedAnime, Is.Empty);
    }

    /// <summary>Kitsu leaves out titles and sends a null abbreviated-titles list for some anime; they read as absent.</summary>
    [Test]
    public void AKitsuAnimeWithoutTitlesConverts() {
        var kitsuAnime = new KitsuSearch.KitsuAnime {
            Id = 8,
            Attributes = new KitsuSearch.Attributes { Slug = "kitsu-slug", EpisodeCount = 12 }
        };

        Anime anime = ClassConversions.ConvertKitsuAnime(kitsuAnime);

        Assert.That(anime.Id, Is.EqualTo(8));
        Assert.That(anime.NumEpisodes, Is.EqualTo(12));
        Assert.That(anime.Title, Is.Null);
        Assert.That(anime.AlternativeTitles!.Synonyms, Is.EqualTo(new[] { "kitsu-slug" }));
    }

    /// <summary>A missing romaji or user-preferred title is left out of an AniList anime's synonyms, not kept as a null entry.</summary>
    [Test]
    public void AMissingAniListTitleIsLeftOutOfTheSynonyms() {
        var media = new AniListSearch.Media { Id = 7, Title = new AniListSearch.Title { English = "English", Romaji = "Romaji" } };

        Anime anime = ClassConversions.ConvertAniListAnime(media);

        Assert.That(anime.AlternativeTitles!.Synonyms, Is.EqualTo(new[] { "Romaji" }));
    }

    /// <summary>A Shikimori anime without synonyms still converts with its list status.</summary>
    [Test]
    public void AShikimoriAnimeWithoutSynonymsConverts() {
        var shikimoriAnime = new ShikimoriAnime {
            Id = "9",
            MalId = "9",
            Name = "Show",
            UserRate = new ShikimoriUserRate { Status = ShikimoriUserRate.StatusEnum.watching, Episodes = 4 }
        };

        Anime anime = ClassConversions.ConvertShikimoriAnime(shikimoriAnime);

        Assert.That(anime.Id, Is.EqualTo(9));
        Assert.That(anime.AlternativeTitles!.Synonyms, Is.Empty);
        Assert.That(anime.MyListStatus!.NumEpisodesWatched, Is.EqualTo(4));
    }

    /// <summary>A detailed Simkl anime without alternative titles has no synonyms; the field is optional.</summary>
    [Test]
    public void ASimklAnimeWithoutAlternativeTitlesHasNoSynonyms() {
        Anime anime = ClassConversions.ConvertSimklAnime(new SimklExtendedMedia { Title = "Show", TotalEpisodes = 12 });

        Assert.That(anime.Title, Is.EqualTo("Show"));
        Assert.That(anime.NumEpisodes, Is.EqualTo(12));
        Assert.That(anime.AlternativeTitles!.Synonyms, Is.Empty);
    }

    /// <summary>A Simkl alternative title without a name is left out of the synonyms, not kept as a null entry.</summary>
    [Test]
    public void ANamelessSimklTitleIsLeftOutOfTheSynonyms() {
        var media = new SimklExtendedMedia { Title = "Show", AllTitles = [new AltTitle { Name = "Other" }, new AltTitle()] };

        Anime anime = ClassConversions.ConvertSimklAnime(media);

        Assert.That(anime.AlternativeTitles!.Synonyms, Is.EqualTo(new[] { "Other" }));
    }

    /// <summary>
    /// Only the missing field became optional: a Simkl detail read that fails or cannot be read is still no anime, never one
    /// with empty titles.
    /// </summary>
    [TestCase(HttpStatusCode.InternalServerError, "")]
    [TestCase(HttpStatusCode.OK, "not json")]
    public async Task AFailedSimklDetailReadIsStillNoAnime(HttpStatusCode status, string body) {
        IHttpClientFactory http = null!;
        Helpers.MockHttpCalls(new List<Helpers.HttpCall> {
            new() { RequestUrlMatch = url => url.EndsWith("/search/id"), ResponseCode = HttpStatusCode.OK, ResponseContent = "[{\"title\":\"Show\",\"ids\":{\"simkl\":9}}]" },
            new() { RequestUrlMatch = url => url.EndsWith("/anime/9"), ResponseCode = status, ResponseContent = body },
            new() { RequestUrlMatch = url => url.Contains("/sync/all-items"), ResponseCode = HttpStatusCode.OK, ResponseContent = "{\"anime\":[]}" }
        }, ref http);
        var user = new UserConfig { UserApiAuth = [new UserApiAuth { Name = ApiName.Simkl, AccessToken = "access", RefreshToken = "refresh" }] };
        var client = new SimklTrackerClient(new jellyfin_anidoki.Trackers.Simkl.SimklApiCalls(http, new NullLoggerFactory(), new Mock<IServerApplicationHost>().Object,
            new Mock<IHttpContextAccessor>().Object, new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object,
            new Dictionary<string, string> { { "simkl-api-key", "client" } }, user));

        Anime? anime = null;
        try {
            anime = await client.GetAnime(new ArmClient.OfflineDatabaseResponse { Anilist = 11 }, "Show");
        } catch (Exception) {
            // A thrown failure is also a failed read.
        }

        Assert.That(anime, Is.Null);
    }

    private static SimklTrackerClient SimklWithList(HttpStatusCode status, string listBody) {
        IHttpClientFactory http = null!;
        Helpers.MockHttpCalls(new List<Helpers.HttpCall> {
            new() { RequestUrlMatch = url => url.EndsWith("/search/id"), ResponseCode = HttpStatusCode.OK, ResponseContent = "[{\"title\":\"Show\",\"ids\":{\"simkl\":9}}]" },
            new() { RequestUrlMatch = url => url.Contains("/anime/9"), ResponseCode = HttpStatusCode.OK, ResponseContent = "{\"title\":\"Show\",\"ids\":{\"simkl\":9},\"total_episodes\":12}" },
            new() { RequestUrlMatch = url => url.Contains("/sync/all-items"), ResponseCode = status, ResponseContent = listBody }
        }, ref http);
        var user = new UserConfig { UserApiAuth = [new UserApiAuth { Name = ApiName.Simkl, AccessToken = "access", RefreshToken = "refresh" }] };
        return new SimklTrackerClient(new jellyfin_anidoki.Trackers.Simkl.SimklApiCalls(http, new NullLoggerFactory(), new Mock<IServerApplicationHost>().Object,
            new Mock<IHttpContextAccessor>().Object, new MemoryCache(new MemoryCacheOptions()), new Mock<IAsyncDelayer>().Object,
            new Dictionary<string, string> { { "simkl-api-key", "client" } }, user));
    }

    /// <summary>
    /// D8's prerequisite: a Simkl list reply that cannot be read is a failed read, never "not on the list", or adding
    /// unlisted anime would add a title the user already has.
    /// </summary>
    [Test]
    public async Task AnUnreadableSimklListIsAFailedReadNotAnEmptyList() {
        var client = SimklWithList(HttpStatusCode.OK, "not json");
        using var calls = TrackerCallScope.Begin();

        await client.GetAnime(new ArmClient.OfflineDatabaseResponse { Anilist = 11 }, "Show");

        Assert.That(calls.Failure, Is.EqualTo(TrackerFailureKind.Transient));
    }

    /// <summary>An empty list, however Simkl sends it, is a list without the title, not a failure.</summary>
    [TestCase("{\"anime\":[]}")]
    [TestCase("{}")]
    [TestCase("null")]
    public async Task AnEmptySimklListIsReadAsNotOnTheList(string body) {
        var client = SimklWithList(HttpStatusCode.OK, body);
        using var calls = TrackerCallScope.Begin();

        Anime? anime = await client.GetAnime(new ArmClient.OfflineDatabaseResponse { Anilist = 11 }, "Show");

        Assert.That(calls.Failure, Is.Null);
        Assert.That(anime, Is.Not.Null);
        Assert.That(anime!.MyListStatus, Is.Null);
    }
}
