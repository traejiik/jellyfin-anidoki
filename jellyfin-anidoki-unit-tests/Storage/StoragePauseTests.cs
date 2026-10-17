using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models.Mal;
using jellyfin_anidoki_unit_tests.Characterization;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Storage;

[TestFixture, NonParallelizable]
public class StoragePauseTests {
    [Test]
    public async Task SyncDoesNothingWhileStorageIsUnavailable() {
        string file = Path.Combine(Path.GetTempPath(), "anidoki-pause-" + Guid.NewGuid());
        File.WriteAllText(file, "fixture");
        try {
            using var harness = new SyncHarness(dataPath: file);
            Assert.That(Plugin.Instance!.StorageError, Is.Not.Null);
            var user = Guid.NewGuid();
            harness.ConfigureUser(user, planToWatchOnly: false, rewatchCompleted: false, ApiName.AniList);
            harness.Tracker(ApiName.AniList).Add(SyncHarness.Entry(SyncHarness.EntryId, "Fixture Show", 12,
                new MyListStatus { Status = Status.Watching, NumEpisodesWatched = 3 }));
            var episode = harness.Library.Episode("Fixture Show", 1, 5,
                new Dictionary<string, string> { ["AniList"] = SyncHarness.EntryId.ToString() });

            string result = await harness.RunSingle(harness.NewUpdater(), episode, user, SyncUpdateSource.Playback, ApiName.AniList);

            Assert.That(result, Is.EqualTo("no writes | no outcomes"));
        } finally {
            File.Delete(file);
        }
    }
}
