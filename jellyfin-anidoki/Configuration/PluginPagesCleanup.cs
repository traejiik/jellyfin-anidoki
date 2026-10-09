#nullable enable
using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace jellyfin_anidoki.Configuration;

internal static class PluginPagesCleanup
{
    private static readonly object FileLock = new();

    internal static void Remove(
        string path, string ownerId, ILogger logger, Action<string>? removeRuntime = null)
    {
        try
        {
            removeRuntime?.Invoke(ownerId);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Could not remove AniDoki's runtime page registration");
        }

        try
        {
            lock (FileLock)
            {
                RemoveStored(path, ownerId, logger);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(error, "Could not remove AniDoki's stored page registration from {Path}", path);
        }
    }

    private static void RemoveStored(string path, string ownerId, ILogger logger)
    {
        if (!File.Exists(path)) return;
        string original = File.ReadAllText(path);
        var config = JObject.Parse(original);
        if (config["pages"] == null) return;
        if (config["pages"] is not JArray pages)
        {
            logger.LogWarning("Plugin Pages configuration has an invalid pages collection at {Path}", path);
            return;
        }

        var owned = pages.OfType<JObject>().Where(page =>
            HasOwnerId(page, "Id", ownerId) || HasOwnerId(page, "id", ownerId)).ToArray();
        if (owned.Length == 0) return;
        foreach (var page in owned) pages.Remove(page);

        string temporary = path + ".anidoki-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, config.ToString(Formatting.Indented));
            if (!string.Equals(File.ReadAllText(path), original, StringComparison.Ordinal))
                throw new IOException("Plugin Pages configuration changed during cleanup; retained the newer file.");
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool HasOwnerId(JObject page, string property, string ownerId) =>
        page[property] is JValue { Type: JTokenType.String } value &&
        string.Equals(value.Value<string>(), ownerId, StringComparison.Ordinal);
}
