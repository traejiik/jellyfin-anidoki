using System.Net.Http;
using jellyfin_anidoki;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Helpers;
using jellyfin_anidoki.Trackers.Mal;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace jellyfin_anidoki_unit_tests;

/// <summary>
/// The ARM client and the anime list as tests build them. With no <paramref name="configuration"/> they read the plugin's
/// settings as the server's registration does; a test that depends on a setting passes its own.
/// </summary>
internal static class IdServices {
    /// <summary>An ARM client whose HTTP factory answers nothing, for tests that never reach ARM.</summary>
    public static ArmClient Arm() => Arm(new Mock<IHttpClientFactory>().Object);

    /// <summary>An anime list whose HTTP factory answers nothing, for tests that never reach the list.</summary>
    public static AnimeListSource AnimeList() => AnimeList(new Mock<IHttpClientFactory>().Object);

    public static ArmClient Arm(IHttpClientFactory http, PluginConfiguration? configuration = null) =>
        new(http, NullLogger<ArmClient>.Instance, () => configuration ?? Plugin.Instance?.PluginConfiguration);

    public static AnimeListSource AnimeList(IHttpClientFactory http, IApplicationPaths? paths = null, PluginConfiguration? configuration = null) =>
        new(http, paths ?? new Mock<IApplicationPaths>().Object, NullLogger<AnimeListSource>.Instance,
            () => configuration ?? Plugin.Instance?.PluginConfiguration);
}
