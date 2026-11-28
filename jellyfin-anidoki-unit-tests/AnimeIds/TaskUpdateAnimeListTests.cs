using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.AnimeIds;
using jellyfin_anidoki.Settings;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.AnimeIds;

/// <summary>Jellyfin shows a scheduled task as Failed only when it throws, so a failed update must throw.</summary>
public class TaskUpdateAnimeListTests {
    private string _directory = null!;

    [SetUp]
    public void SetUp() {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-update-task-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private TaskUpdateAnimeList Task(HttpStatusCode code, string body) {
        var http = new Mock<IHttpClientFactory>();
        http.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new Answering(code, body), disposeHandler: false));
        var configuration = new PluginConfiguration { animeListSaveLocation = _directory };
        return new TaskUpdateAnimeList(new AnimeListSource(http.Object, new Mock<IApplicationPaths>().Object,
            NullLogger<AnimeListSource>.Instance, () => configuration));
    }

    [Test]
    public void AFailedUpdateFailsTheTask() {
        var task = Task(HttpStatusCode.InternalServerError, "");

        Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(new Progress<double>(), CancellationToken.None));
    }

    [Test]
    public async Task AnUpToDateListCompletesTheTask() {
        await File.WriteAllTextAsync(Path.Combine(_directory, "anime-list-full.xml"), "abc");
        var task = Task(HttpStatusCode.OK, "{\"name\":\"anime-list-full.xml\",\"size\":3,\"download_url\":\"https://example.test/list.xml\"}");

        Assert.DoesNotThrowAsync(() => task.ExecuteAsync(new Progress<double>(), CancellationToken.None));
    }

    private sealed class Answering(HttpStatusCode code, string body) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }
}
