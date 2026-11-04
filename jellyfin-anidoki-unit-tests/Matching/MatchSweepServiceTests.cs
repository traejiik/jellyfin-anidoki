using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using jellyfin_anidoki.Matching;
using Match = jellyfin_anidoki.Matching.Match;
using jellyfin_anidoki.Storage;
using jellyfin_anidoki_unit_tests.Operations;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using static jellyfin_anidoki_unit_tests.Matching.MatchTestSupport;

namespace jellyfin_anidoki_unit_tests.Matching;

[TestFixture]
public class MatchSweepServiceTests {
    private static readonly MatchSource Source = new(SourceKey, MatchSourceKind.Season, "Show", new Dictionary<string, string>());

    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = OperationTestSupport.NewDirectory();

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private static MatchSweepService Service(MatchStore store, params Guid[] users) {
        var manager = new Mock<IUserManager>();
        var known = new List<User>();
        foreach (Guid id in users)
            known.Add(new User("fixture", "auth", "reset") { Id = id });
        manager.Setup(m => m.GetUsers()).Returns(known);
        return new MatchSweepService(store, manager.Object, NullLogger<MatchSweepService>.Instance);
    }

    [Test]
    public async Task StartingRemovesOverridesOfUsersJellyfinNoLongerHas() {
        var store = new MatchStore(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), TimeProvider.System);
        Guid kept = Guid.NewGuid();
        var keptScope = new MatchScope(SharedScope.Provider, SourceKey, kept);
        var goneScope = new MatchScope(SharedScope.Provider, SourceKey, Guid.NewGuid());
        store.Save(Source, new Match(keptScope, [Range(1, 12, A)], []), 0, "user");
        store.Save(Source, new Match(goneScope, [Range(1, 12, B)], []), 0, "user");

        await Service(store, kept).StartAsync(CancellationToken.None);

        Assert.That(store.Get(keptScope), Is.Not.Null);
        Assert.That(store.Get(goneScope), Is.Null);
    }

    [Test]
    public void AnyFailureDoesNotStopStartup() {
        var store = new MatchStore(new AniDokiDatabase(Path.Combine(_directory, "anidoki.db")), TimeProvider.System);
        var manager = new Mock<IUserManager>();
        manager.Setup(m => m.GetUsers()).Throws(new FormatException("fixture"));
        var service = new MatchSweepService(store, manager.Object, NullLogger<MatchSweepService>.Instance);

        Assert.DoesNotThrowAsync(() => service.StartAsync(CancellationToken.None));
    }

    [Test]
    public void AnUnavailableDatabaseDoesNotStopStartup() {
        string file = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(file, "fixture");
        var store = new MatchStore(new AniDokiDatabase(Path.Combine(file, "anidoki.db")), TimeProvider.System);

        Assert.DoesNotThrowAsync(() => Service(store, Guid.NewGuid()).StartAsync(CancellationToken.None));
    }
}
