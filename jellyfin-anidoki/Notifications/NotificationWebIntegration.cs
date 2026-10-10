#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using Newtonsoft.Json.Linq;

namespace jellyfin_anidoki.Notifications;

public static class NotificationWebIntegration
{
    public static readonly Guid TransformationId = Guid.Parse("5905408d-50d7-4b12-950d-d59cb95ed918");
    internal static string BasePath { get; set; } = "";
    private static Type? Interface => AssemblyLoadContext.All.SelectMany(c => c.Assemblies).Distinct()
        .Select(a => a.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface", false)).FirstOrDefault(t => t != null);
    internal static bool Register(Type? pluginInterface = null)
    {
        var method = (pluginInterface ?? Interface)?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static, null, [typeof(JObject)], null);
        if (method == null) return false;
        method.Invoke(null, [new JObject {
            ["id"] = TransformationId.ToString(), ["fileNamePattern"] = "index.html",
            ["callbackAssembly"] = typeof(NotificationWebIntegration).Assembly.FullName,
            ["callbackClass"] = typeof(NotificationWebIntegration).FullName, ["callbackMethod"] = nameof(Transform)
        }]);
        return true;
    }
    public static void Remove() => Interface?.GetMethod("RemoveTransformation", BindingFlags.Public | BindingFlags.Static,
        null, [typeof(Guid)], null)?.Invoke(null, [TransformationId]);
    public static string Transform(JObject payload)
    {
        string original = payload.Value<string>("contents") ?? "";
        if (original.Contains("id=\"anidoki-notification-bootstrap\"", StringComparison.Ordinal) ||
            original.Contains("id='anidoki-notification-bootstrap'", StringComparison.Ordinal)) return original;
        int position = original.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (position < 0) return original;
        var path = BasePath.Trim('/');
        var source = (path.Length == 0 ? "" : "/" + path) + "/AniDoki/assets/notifications.js?v=" +
            typeof(NotificationWebIntegration).Assembly.ManifestModule.ModuleVersionId.ToString("N");
        var tag = "<script id=\"anidoki-notification-bootstrap\" type=\"module\" src=\"" + WebUtility.HtmlEncode(source) + "\"></script>";
        return original.Insert(position, tag);
    }
}
