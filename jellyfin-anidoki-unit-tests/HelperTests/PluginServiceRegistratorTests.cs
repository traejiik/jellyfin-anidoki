using jellyfin_anidoki;
using jellyfin_anidoki.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

/// <summary>The ID lookups are one shared instance each, so every caller goes through the same client and the same list.</summary>
public class PluginServiceRegistratorTests {
    private static ServiceProvider Services() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IApplicationPaths>().Object);
        new PluginServiceRegistrator().RegisterServices(services, new Mock<IServerApplicationHost>().Object);
        return services.BuildServiceProvider();
    }

    [Test]
    public void TheIdLookupsAreSingletons() {
        using ServiceProvider provider = Services();

        Assert.That(provider.GetRequiredService<ArmClient>(), Is.SameAs(provider.GetRequiredService<ArmClient>()));
        Assert.That(provider.GetRequiredService<AnimeListSource>(), Is.SameAs(provider.GetRequiredService<AnimeListSource>()));
    }

    [Test]
    public void TheAnimeListIndexIsTheAnimeListsOwn() {
        using ServiceProvider provider = Services();

        Assert.That(provider.GetRequiredService<AnimeListIndex>(), Is.SameAs(provider.GetRequiredService<AnimeListSource>().Index));
    }
}
