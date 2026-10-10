using jellyfin_anidoki.Notifications;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.NotificationTests;

[TestFixture]
public class NotificationWebIntegrationTests
{
    public static class FixtureInterface
    {
        public static JObject? Payload;
        public static void RegisterTransformation(JObject payload) => Payload = payload;
    }
    [Test] public void RegisterJoinsTheExistingIndexPipelineInsteadOfCompetingRegex()
    {
        Assert.That(NotificationWebIntegration.Register(typeof(FixtureInterface)), Is.True);
        Assert.That(FixtureInterface.Payload!["fileNamePattern"]!.Value<string>(), Is.EqualTo("index.html"));
        Assert.That(FixtureInterface.Payload["id"]!.Value<string>(), Is.EqualTo(NotificationWebIntegration.TransformationId.ToString()));
    }

    [Test] public void TransformPreservesOriginalAndOtherScriptsAndIsIdempotent()
    {
        NotificationWebIntegration.BasePath = "/jellyfin";
        string original = "<HTML><body><script src='other.js'></script>hello</BODY></HTML>";
        string result = NotificationWebIntegration.Transform(new JObject { ["contents"] = original });
        Assert.That(result, Does.Contain("/jellyfin/AniDoki/assets/notifications.js"));
        Assert.That(result, Does.Contain("<script src='other.js'></script>hello"));
        Assert.That(NotificationWebIntegration.Transform(new JObject { ["contents"] = result }), Is.EqualTo(result));
    }
    [Test] public void UnsupportedInputRemainsUnchanged()
    {
        const string original = "<html>fragment</html>";
        Assert.That(NotificationWebIntegration.Transform(new JObject { ["contents"] = original }), Is.EqualTo(original));
    }
    [Test] public void RootPathAndNoOptionalAssemblyRemainSafe()
    {
        NotificationWebIntegration.BasePath = "";
        Assert.That(NotificationWebIntegration.Transform(new JObject { ["contents"] = "<body></body>" }), Does.Contain("src=\"/AniDoki/assets/notifications.js"));
        Assert.That(NotificationWebIntegration.Register(), Is.False);
        Assert.DoesNotThrow(() => NotificationWebIntegration.Remove());
    }
}
