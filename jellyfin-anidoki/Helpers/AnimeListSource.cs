using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Serialization;
using jellyfin_anidoki.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Helpers;

/// <summary>
/// The anime list file (Anime-Lists' <c>anime-list-full.xml</c>): where it is kept, keeping it up to date, downloading it
/// when it is missing, and reading it.
/// </summary>
/// <param name="configuration">The plugin settings, read on every use so a changed save location applies at once.</param>
public sealed class AnimeListSource {
    private const string FileName = "anime-list-full.xml";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<AnimeListSource> _logger;
    private readonly Func<PluginConfiguration?> _configuration;

    public AnimeListSource(IHttpClientFactory httpClientFactory, IApplicationPaths applicationPaths, ILogger<AnimeListSource> logger,
        Func<PluginConfiguration?> configuration) {
        _httpClientFactory = httpClientFactory;
        _applicationPaths = applicationPaths;
        _logger = logger;
        _configuration = configuration;
        Index = new AnimeListIndex(() => Path);
    }

    /// <summary>Where the anime list file is kept: the admin's chosen folder, else Jellyfin's cache.</summary>
    public string Path {
        get {
            string? saveLocation = _configuration()?.animeListSaveLocation;
            return string.IsNullOrEmpty(saveLocation)
                ? System.IO.Path.Combine(_applicationPaths.CachePath, "anidoki", FileName)
                : System.IO.Path.Combine(saveLocation, FileName);
        }
    }

    /// <summary>The list's movie IDs, read from the same file as <see cref="Read"/>.</summary>
    public AnimeListIndex Index { get; }

    /// <summary>The whole list, downloaded first if the file is missing.</summary>
    /// <returns>Null when the file cannot be downloaded or read.</returns>
    public async Task<AnimeListHelpers.AnimeListXml?> Read() {
        string path = Path;
        try {
            FileInfo animeListXml = new FileInfo(path);
            if (!animeListXml.Exists) {
                _logger.LogInformation("The anime list was not found at {Path}; downloading it", path);
                if (await Update()) {
                    _logger.LogInformation("Downloaded the anime list to {Path}", path);
                }
            }

            long started = Stopwatch.GetTimestamp();
            using (var stream = File.OpenRead(path)) {
                var serializer = new XmlSerializer(typeof(AnimeListHelpers.AnimeListXml));
                var list = (AnimeListHelpers.AnimeListXml?)serializer.Deserialize(stream);
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Read the anime list in {ElapsedMs} ms", LogText.ElapsedMs(started));
                return list;
            }
        } catch (Exception e) {
            _logger.LogError(e, "Could not read the anime list at {Path}; run the AniDoki Update Anime List task to download it again", path);
            return null;
        }
    }

    /// <summary>
    /// Update the anime list file to the latest version.
    /// </summary>
    /// <returns>False when the repository could not be asked or the new list could not be downloaded.</returns>
    public async Task<bool> Update() {
        long started = Stopwatch.GetTimestamp();
        bool updated = await UpdateFile();
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Checked the anime list for an update in {ElapsedMs} ms", LogText.ElapsedMs(started));
        return updated;
    }

    private async Task<bool> UpdateFile() {
        string path = Path;
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client.GetAsync("https://api.github.com/repos/Anime-Lists/anime-lists/contents/anime-list-full.xml");
        if (response.IsSuccessStatusCode) {
            using var streamReader = new StreamReader(await response.Content.ReadAsStreamAsync());
            string streamText = await streamReader.ReadToEndAsync();

            GitFile? formattedResponse = JsonSerializer.Deserialize<GitFile>(streamText);

            if (formattedResponse == null) {
                _logger.LogWarning("Could not update the anime list; the repository's file listing could not be read");
                return false;
            }

            if (FileSizeHasChanged(path, formattedResponse.Size)) {
                return await GetLatestAnimeList(client, path, formattedResponse.DownloadUrl);
            }
        } else {
            _logger.LogWarning("Could not update the anime list; the repository returned {StatusCode}", (int)response.StatusCode);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Check if the anime list file size has changed.
    /// </summary>
    /// <param name="path">The anime list file.</param>
    /// <param name="fileSize">File size to compare with.</param>
    private static bool FileSizeHasChanged(string path, int fileSize) {
        FileInfo animeListFile = new FileInfo(path);
        if (animeListFile.Exists)
            return animeListFile.Length != fileSize;
        return true;
    }

    /// <summary>
    /// Download the latest version of the anime list.
    /// </summary>
    /// <param name="httpClient">HTTP client.</param>
    /// <param name="path">Where to write the file.</param>
    /// <param name="downloadUrl">Download URL of the anime list file.</param>
    /// <returns>Whether the file was written.</returns>
    private async Task<bool> GetLatestAnimeList(HttpClient httpClient, string path, string? downloadUrl) {
        try {
            Directory.CreateDirectory(path.EndsWith(FileName) ? path.Replace(FileName, "") : path);
            await using (var response = await httpClient.GetStreamAsync(downloadUrl)) {
                await using (var fileStream = new FileStream(path, FileMode.OpenOrCreate)) {
                    fileStream.SetLength(0);
                    await response.CopyToAsync(fileStream);
                }
            }
            return true;
        } catch (Exception e) {
            _logger.LogError(e, "Could not download the anime list");
            return false;
        }
    }

    public class GitFile {
        [JsonPropertyName("name")] public string? Name { get; set; }

        [JsonPropertyName("size")] public int Size { get; set; }

        [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    }
}
