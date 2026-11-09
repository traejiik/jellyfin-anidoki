using System;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Serialization;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki_unit_tests.Characterization;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

[TestFixture, NonParallelizable]
public class AnimeLibrariesConfigurationTests {
    private static PluginConfiguration RoundTrip(PluginConfiguration configuration) {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, configuration);
        using var reader = new StringReader(writer.ToString());
        return (PluginConfiguration)serializer.Deserialize(reader)!;
    }

    [Test]
    public void AnimeLibrariesSurviveTheConfigurationFile() {
        Guid anime = Guid.NewGuid(), mixed = Guid.NewGuid();
        var read = RoundTrip(new PluginConfiguration {
            AnimeLibraries = [new AnimeLibrary { LibraryId = anime, Mode = AnimeLibraryMode.Dedicated }, new AnimeLibrary { LibraryId = mixed, Mode = AnimeLibraryMode.Mixed }]
        });

        Assert.That(read.AnimeLibraries, Has.Length.EqualTo(2));
        Assert.That((read.AnimeLibraries[0].LibraryId, read.AnimeLibraries[0].Mode), Is.EqualTo((anime, AnimeLibraryMode.Dedicated)));
        Assert.That((read.AnimeLibraries[1].LibraryId, read.AnimeLibraries[1].Mode), Is.EqualTo((mixed, AnimeLibraryMode.Mixed)));
    }

    [Test]
    public void AConfigurationFromBeforeTheSettingReadsAsEmpty() {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader("<?xml version=\"1.0\"?><PluginConfiguration></PluginConfiguration>");
        var read = (PluginConfiguration)serializer.Deserialize(reader)!;

        Assert.That(read.AnimeLibraries, Is.Empty);
    }

    /// <summary>
    /// The fixture episode has no library folder, so it is in no admin library. The second run, with the list empty
    /// again, takes the per-user path and writes, which shows the first result came from the admin list.
    /// </summary>
    [Test]
    public async Task OnceTheAdminChoosesAnItemOutsideTheirLibrariesIsNotSynced() {
        using var harness = new SyncHarness();
        Guid user = Guid.NewGuid();
        harness.ConfigureUser(user, false, false, ApiName.AniList);
        harness.SeedAll(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", 12, new MyListStatus { Status = Status.Watching, NumEpisodesWatched = 3 }));
        var episode = harness.Library.Episode("Fixture Show", 1, 5);

        harness.Configuration.AnimeLibraries = [new AnimeLibrary { LibraryId = Guid.NewGuid(), Mode = AnimeLibraryMode.Dedicated }];
        await harness.NewUpdater().Run(episode, user, true, null, SyncUpdateSource.Playback);
        Assert.That(harness.Tracker(ApiName.AniList).Writes, Is.Empty);

        harness.Configuration.AnimeLibraries = [];
        await harness.NewUpdater().Run(episode, user, true, null, SyncUpdateSource.Playback);
        Assert.That(harness.Tracker(ApiName.AniList).Writes, Has.Count.EqualTo(1));
    }
}
