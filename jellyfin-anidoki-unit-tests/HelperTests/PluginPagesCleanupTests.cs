using System;
using System.IO;
using jellyfin_anidoki.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.HelperTests;

[TestFixture]
public class PluginPagesCleanupTests
{
    private string _directory = null!;
    private string _path = null!;
    private const string Owner = "jellyfin_anidoki";

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "anidoki-pages-" + Guid.NewGuid());
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "config.json");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    private void Remove(Action<string>? runtime = null) =>
        PluginPagesCleanup.Remove(_path, Owner, NullLogger.Instance, runtime);

    [Test]
    public void RemovesAllOwnedEntriesAndPreservesOtherPagesAndProperties()
    {
        File.WriteAllText(_path, """
        {"otherSetting":42,"pages":[
          {"Id":"jellyfin_anidoki","Url":"/AniDoki/settings"},
          {"Id":"other.plugin","Url":"/other/settings","custom":true},
          {"id":"jellyfin_anidoki","url":"/AniDoki/settings"}
        ]}
        """);

        Remove();

        var result = JObject.Parse(File.ReadAllText(_path));
        var pages = (JArray)result["pages"]!;
        Assert.That((int)result["otherSetting"]!, Is.EqualTo(42));
        Assert.That(pages.Count, Is.EqualTo(1));
        Assert.That((string)pages[0]["Id"]!, Is.EqualTo("other.plugin"));
        Assert.That((bool)pages[0]["custom"]!, Is.True);
    }

    [TestCase("not json")]
    [TestCase("{\"pages\":{\"Id\":\"jellyfin_anidoki\"},\"keep\":true}")]
    [TestCase("{\"keep\":true}")]
    [TestCase("{\"pages\":[{\"Id\":\"other.plugin\"}],\"keep\":true}")]
    [TestCase("{\"pages\":[{\"Id\":{\"nested\":true}},{\"id\":[\"jellyfin_anidoki\"]}],\"keep\":true}")]
    [TestCase("{\"pages\":[{\"Id\":\"jellyfin_anidoki.other\"}],\"keep\":true}")]
    public void DoesNotRewriteMalformedOrUnrelatedConfiguration(string original)
    {
        File.WriteAllText(_path, original);
        Assert.DoesNotThrow(() => Remove());
        Assert.That(File.ReadAllText(_path), Is.EqualTo(original));
    }

    [Test]
    public void MissingConfigurationDoesNotCreateAFile()
    {
        Remove();
        Assert.That(File.Exists(_path), Is.False);
    }

    [Test]
    public void RuntimeFailureDoesNotPreventStoredCleanup()
    {
        File.WriteAllText(_path, "{\"pages\":[{\"Id\":\"jellyfin_anidoki\"}]}");
        Remove(_ => throw new InvalidOperationException("Runtime integration unavailable"));
        Assert.That(((JArray)JObject.Parse(File.ReadAllText(_path))["pages"]!).Count,
            Is.EqualTo(0));
    }

    [Test]
    public void MalformedStoredConfigurationDoesNotPreventRuntimeCleanup()
    {
        File.WriteAllText(_path, "not json");
        string? removed = null;
        Remove(id => removed = id);
        Assert.That(removed, Is.EqualTo(Owner));
        Assert.That(File.ReadAllText(_path), Is.EqualTo("not json"));
    }

    [Test]
    public void RepeatedCleanupIsHarmless()
    {
        File.WriteAllText(_path, "{\"pages\":[{\"Id\":\"jellyfin_anidoki\"}]}");
        Remove();
        string first = File.ReadAllText(_path);
        Remove();
        Assert.That(File.ReadAllText(_path), Is.EqualTo(first));
    }
}
