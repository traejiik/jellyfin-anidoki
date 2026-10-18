using System;
using System.IO;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Notifications;
using jellyfin_anidoki.Operations;
using jellyfin_anidoki.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace jellyfin_anidoki_unit_tests.Operations;

internal sealed class MutableTime(DateTimeOffset now) : TimeProvider {
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class OperationTestSupport {
    public static (OperationStore Store, OperationRunner Runner) Create(string directory, TimeProvider? clock = null) {
        var store = new OperationStore(new AniDokiDatabase(Path.Combine(directory, "anidoki.db")), clock ?? TimeProvider.System);
        return (store, new OperationRunner(store, NullLogger<OperationRunner>.Instance));
    }

    public static ProviderWriteOutcome Outcome(OutcomeKind kind, bool meaningful = false) =>
        new(ApiName.AniList, "500", "Fixture Show", kind, null, null, false, "progress", meaningful);

    public static string NewDirectory() {
        string directory = Path.Combine(Path.GetTempPath(), "anidoki-operations-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static async Task WaitUntil(Func<bool> condition) {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition was not met within 10 seconds.");
            await Task.Delay(20);
        }
    }
}
