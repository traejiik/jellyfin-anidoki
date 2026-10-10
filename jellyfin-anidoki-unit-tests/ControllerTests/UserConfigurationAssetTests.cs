using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.ControllerTests;

public partial class UserConfigurationTests
{
    [TestCase("notifications.js")]
    [TestCase("notification-state.js")]
    public void PublicNotificationModulesAreEmbeddedAndUncachedWithoutPluginPages(string asset)
    {
        _plugin.Configuration.enableUserPages = false;
        var file = (FileStreamResult)_controller.GetAsset(asset);
        using var stream = file.FileStream;
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        Assert.That(file.ContentType, Does.Contain("javascript"));
        Assert.That(source, Does.Contain("export function"));
        Assert.That(source, Does.Not.Contain("fixture-secret").And.Not.Contain("fixture-client"));
        Assert.That(_controller.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
    }

    [TestCase("provider-anilist.svg")]
    [TestCase("provider-mal.svg")]
    [TestCase("provider-kitsu.svg")]
    [TestCase("provider-shikimori.svg")]
    [TestCase("provider-simkl.svg")]
    public void PublicProviderSvgAssetsAreEmbeddedAndContainOnlyLocalDrawingData(string asset)
    {
        _plugin.Configuration.enableUserPages = false;
        var result = _controller.GetAsset(asset);
        Assert.That(result, Is.TypeOf<FileStreamResult>());
        var file = (FileStreamResult)result;
        using var stream = file.FileStream;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var document = XDocument.Load(reader);
        Assert.Multiple(() => {
            Assert.That(file.ContentType, Is.EqualTo("image/svg+xml"));
            Assert.That(document.Root!.Name, Is.EqualTo(XName.Get("svg", "http://www.w3.org/2000/svg")));
            Assert.That(document.Descendants().Any(element => element.Name.LocalName == "path"), Is.True);
            Assert.That(document.Descendants().Any(element => element.Name.LocalName is "script" or "foreignObject" or "image" or "style"), Is.False);
            Assert.That(document.Descendants().Attributes().Any(attribute =>
                attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                attribute.Name.LocalName is "href" or "src" ||
                attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase)), Is.False);
        });
    }

    [Test]
    public void PublicAnnictLogoIsEmbeddedAsPngWhenUserPagesAreDisabled()
    {
        _plugin.Configuration.enableUserPages = false;
        var result = _controller.GetAsset("provider-annict.png");
        Assert.That(result, Is.TypeOf<FileStreamResult>());
        var file = (FileStreamResult)result;
        using var stream = file.FileStream;
        var signature = new byte[8];
        Assert.That(stream.Read(signature), Is.EqualTo(8));
        Assert.Multiple(() => {
            Assert.That(file.ContentType, Is.EqualTo("image/png"));
            Assert.That(signature, Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.That(stream.Length, Is.GreaterThan(8));
        });
    }

    [TestCase("provider-unknown.svg")]
    [TestCase("provider-annict.svg")]
    [TestCase("../Images/provider-anilist.svg")]
    [TestCase("provider-anilist.svg/../CommonJs.js")]
    [TestCase("jellyfin_anidoki.Configuration.Images.provider-anilist.svg")]
    public void PublicProviderAssetWhitelistRejectsUnknownNamesAndResourcePaths(string asset)
    {
        Assert.That(_controller.GetAsset(asset), Is.TypeOf<NotFoundResult>());
    }
}
