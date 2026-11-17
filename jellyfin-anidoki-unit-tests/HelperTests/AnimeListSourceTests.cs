using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki_unit_tests.LoggingTests;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

/// <summary>Where the anime list is kept, how it is downloaded when missing, and how it is read.</summary>
public class AnimeListSourceTests {
    private const string Listing = "{\"name\":\"anime-list-full.xml\",\"size\":1,\"download_url\":\"https://example.test/anime-list-full.xml\"}";
    private const string List = "<anime-list><anime anidbid=\"1\" tvdbid=\"2\" defaulttvdbseason=\"1\" tmdbid=\"3\" /></anime-list>";

    private string _directory = null!;
    private PluginConfiguration _configuration = null!;
    private CapturedLogs _logs = null!;
    private readonly List<Uri> _requests = new();

    [SetUp]
    public void SetUp() {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-anime-list-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _configuration = new PluginConfiguration { animeListSaveLocation = _directory };
        _logs = new CapturedLogs();
        _requests.Clear();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private string File => Path.Combine(_directory, "anime-list-full.xml");

    /// <summary>Answers the repository listing, then the download, with these responses in order.</summary>
    private AnimeListSource Source(params (HttpStatusCode Code, string Body)[] responses) {
        int sent = 0;
        var handler = new Answering(request => {
            _requests.Add(request.RequestUri!);
            var (code, body) = responses[Math.Min(++sent, responses.Length) - 1];
            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        });
        var http = new Mock<IHttpClientFactory>();
        http.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new AnimeListSource(http.Object, new Mock<IApplicationPaths>().Object, _logs.For<AnimeListSource>(), () => _configuration);
    }

    [Test]
    public async Task AnUpdateTheRepositoryRefusesReportsFailure() {
        bool updated = await Source((HttpStatusCode.InternalServerError, "")).Update();

        Assert.That(updated, Is.False);
    }

    [Test]
    public async Task AnUpdateWhoseDownloadFailsReportsFailure() {
        bool updated = await Source((HttpStatusCode.OK, Listing), (HttpStatusCode.InternalServerError, "")).Update();

        Assert.That(updated, Is.False);
        Assert.That(System.IO.File.Exists(File), Is.False);
    }

    [Test]
    public async Task AnUpdateDownloadsAChangedList() {
        bool updated = await Source((HttpStatusCode.OK, Listing), (HttpStatusCode.OK, List)).Update();

        Assert.That(updated, Is.True);
        Assert.That(await System.IO.File.ReadAllTextAsync(File), Is.EqualTo(List));
    }

    /// <summary>The git blob hash GitHub reports for a file with this content.</summary>
    private static string BlobSha(string content) {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"blob {bytes.Length}\0");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData([.. header, .. bytes]));
    }

    private static string ListingOf(int size, string? sha) =>
        $"{{\"name\":\"anime-list-full.xml\",\"size\":{size},{(sha == null ? "" : $"\"sha\":\"{sha}\",")}\"download_url\":\"https://example.test/anime-list-full.xml\"}}";

    /// <summary>An upstream edit that keeps the file's length (one digit for another) is still downloaded.</summary>
    [Test]
    public async Task AnUpdateDownloadsASameSizeListWithADifferentHash() {
        await System.IO.File.WriteAllTextAsync(File, "abc");

        bool updated = await Source((HttpStatusCode.OK, ListingOf(3, BlobSha("abd"))), (HttpStatusCode.OK, "abd")).Update();

        Assert.That(updated, Is.True);
        Assert.That(await System.IO.File.ReadAllTextAsync(File), Is.EqualTo("abd"));
    }

    [Test]
    public async Task AnUpdateSkipsAListWithTheSameHash() {
        await System.IO.File.WriteAllTextAsync(File, "abc");

        bool updated = await Source((HttpStatusCode.OK, ListingOf(3, BlobSha("abc"))), (HttpStatusCode.OK, "unexpected")).Update();

        Assert.That(updated, Is.True);
        Assert.That(_requests, Has.Count.EqualTo(1));
        Assert.That(await System.IO.File.ReadAllTextAsync(File), Is.EqualTo("abc"));
    }

    /// <summary>A listing without a hash falls back to comparing the length, as before.</summary>
    [Test]
    public async Task AnUpdateWithoutAHashComparesTheLength() {
        await System.IO.File.WriteAllTextAsync(File, "abc");

        bool updated = await Source((HttpStatusCode.OK, ListingOf(3, null)), (HttpStatusCode.OK, "unexpected")).Update();

        Assert.That(updated, Is.True);
        Assert.That(_requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AMissingListThatCannotBeDownloadedIsNotReportedAsDownloaded() {
        var list = await Source((HttpStatusCode.InternalServerError, "")).Read();

        Assert.That(list, Is.Null);
        Assert.That(_logs.At(LogLevel.Information).Where(line => line.StartsWith("Downloaded the anime list")), Is.Empty);
        Assert.That(_logs.At(LogLevel.Error), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task AMissingListIsDownloadedThenRead() {
        var list = await Source((HttpStatusCode.OK, Listing), (HttpStatusCode.OK, List)).Read();

        Assert.That(list?.Anime?.Single().Anidbid, Is.EqualTo("1"));
        Assert.That(_requests.Select(uri => uri.Host), Is.EqualTo(new[] { "api.github.com", "example.test" }));
        Assert.That(_logs.At(LogLevel.Information), Is.EqualTo(new[] {
            $"The anime list was not found at {File}; downloading it",
            $"Downloaded the anime list to {File}"
        }));
        Assert.That(_logs.Entries.Select(entry => entry.Category).Distinct(), Is.EqualTo(new[] { typeof(AnimeListSource).FullName }));
    }

    [Test]
    public async Task APresentListIsReadWithoutAsking() {
        await System.IO.File.WriteAllTextAsync(File, List);

        var list = await Source((HttpStatusCode.InternalServerError, "")).Read();

        Assert.That(list?.Anime?.Single().Tvdbid, Is.EqualTo("2"));
        Assert.That(_requests, Is.Empty);
    }

    [Test]
    public async Task AnUnreadableListIsNoList() {
        await System.IO.File.WriteAllTextAsync(File, "<anime-list>");

        Assert.That(await Source((HttpStatusCode.InternalServerError, "")).Read(), Is.Null);
        Assert.That(_logs.At(LogLevel.Error), Has.Length.EqualTo(1));
    }

    [Test]
    public void TheListIsKeptWhereTheSettingsSayAtTheTime() {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.CachePath).Returns("/cache");
        var source = new AnimeListSource(new Mock<IHttpClientFactory>().Object, paths.Object, _logs.For<AnimeListSource>(), () => _configuration);

        Assert.That(source.Path, Is.EqualTo(File));
        _configuration.animeListSaveLocation = null!;
        Assert.That(source.Path, Is.EqualTo(Path.Combine("/cache", "anidoki", "anime-list-full.xml")));
    }

    [Test]
    public async Task TheIndexReadsTheSameFile() {
        await System.IO.File.WriteAllTextAsync(File, List);
        AnimeListSource source = Source((HttpStatusCode.InternalServerError, ""));

        Assert.That(source.Index.AniDbIdForMovie("3"), Is.EqualTo(1));
        _configuration.animeListSaveLocation = Path.Combine(_directory, "elsewhere");
        Assert.That(source.Index.AniDbIdForMovie("3"), Is.Null);
    }

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
