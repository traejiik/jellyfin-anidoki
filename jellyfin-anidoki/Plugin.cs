using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;


namespace jellyfin_anidoki {
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages {
        private readonly ILogger<Plugin> _logger;

        private readonly SecretStore _secrets;

        public Plugin(IApplicationPaths applicationPaths,
            IServerConfigurationManager serverConfigurationManager,
            IXmlSerializer xmlSerializer,
            ILogger<Plugin> logger) : base(applicationPaths, xmlSerializer) {
            _logger = logger;
            Instance = this;
            Database = AniDokiDatabase.ForDataPath(applicationPaths.DataPath);
            _secrets = new SecretStore(Database);
            LoadCredentials();
            try {
                if (PluginConfiguration.enableUserPages)
                    CheckPluginPages(applicationPaths, serverConfigurationManager);
                else
                    RemovePluginPages(applicationPaths);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
                _logger.LogWarning(error, "Could not initialize AniDoki user-page registration");
            }
        }

        public override string Name => "AniDoki";
        public override Guid Id => Guid.Parse("dceb799c-238e-4a33-aa5e-14fc0b1efe9d");
        public override string Description => "Synchronize anime watch status between Jellyfin and anime tracking sites.";
        public PluginConfiguration PluginConfiguration => Configuration;
        public static Plugin? Instance { get; private set; }

        /// <summary>The plugin database. Check <see cref="StorageError"/> before relying on it.</summary>
        public AniDokiDatabase Database { get; }

        /// <summary>
        /// Why stored credentials cannot be used, or null. While set, syncs pause and saves are refused, so a save can
        /// never drop credentials that are only in the old XML file.
        /// </summary>
        public string? StorageError { get; private set; }

        private bool _loadingConfiguration;

        private void LoadCredentials() {
            // Read old credentials before touching Configuration: if the XML is unreadable, Jellyfin replaces it with defaults on load.
            Dictionary<SecretKey, string> legacy = new();
            string? legacyError = null;
            try {
                legacy = LegacySecrets.Read(ConfigurationFilePath);
            } catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException) {
                legacyError = $"Credentials in {ConfigurationFilePath} could not be read: {error.Message}";
            }
            // Jellyfin may save default settings while loading the file. Those saves must not replace stored credentials.
            _loadingConfiguration = true;
            try {
                _ = Configuration;
            } finally {
                _loadingConfiguration = false;
            }
            StorageError = Database.Failure ?? legacyError;
            if (StorageError != null) {
                _logger.LogError("AniDoki storage is unavailable; tracker syncs are paused and settings cannot be saved: {StorageError}", StorageError);
                return;
            }
            try {
                var credentials = _secrets.ReadAll();
                // Anything still in the XML was written after the last move (for example by an older version), so it wins.
                foreach (var (key, value) in legacy)
                    credentials[key] = value;
                lock (Configuration)
                    SecretOverlay.Apply(Configuration, credentials);
                if (legacy.Count == 0)
                    return;
                SaveConfiguration();
                var stored = _secrets.ReadAll();
                if (legacy.Any(pair => !stored.TryGetValue(pair.Key, out var value) || value != pair.Value)) {
                    StorageError = "Credentials moved from the plugin configuration could not be confirmed in the AniDoki database";
                    _logger.LogError("AniDoki storage is unavailable; tracker syncs are paused and settings cannot be saved: {StorageError}", StorageError);
                    return;
                }
                _logger.LogInformation("Moved {CredentialCount} credentials from the plugin configuration into the AniDoki database", legacy.Count);
            } catch (Exception error) when (error is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or IOException) {
                StorageError = $"Credentials could not be loaded: {error.Message}";
                _logger.LogError("AniDoki storage is unavailable; tracker syncs are paused and settings cannot be saved: {StorageError}", StorageError);
            }
        }

        public override void SaveConfiguration() => SaveConfiguration(Configuration);

        /// <summary>Writes credentials to the database first, then the XML; if the database write fails, nothing is written.</summary>
        public override void SaveConfiguration(PluginConfiguration config) {
            if (_loadingConfiguration) {
                base.SaveConfiguration(config);
                return;
            }
            if (StorageError != null)
                throw new InvalidOperationException($"AniDoki settings were not saved because storage is unavailable: {StorageError}");
            _secrets.ReplaceAll(SecretOverlay.Extract(config));
            base.SaveConfiguration(config);
        }

        /// <summary>The admin page posts configuration without credentials; carry the current ones over before saving.</summary>
        public override void UpdateConfiguration(BasePluginConfiguration configuration) {
            if (configuration is PluginConfiguration incoming) {
                lock (Configuration)
                    SecretOverlay.Apply(incoming, SecretOverlay.Extract(Configuration));
            }
            base.UpdateConfiguration(configuration);
        }

        public void CheckPluginPages(IApplicationPaths applicationPaths, IServerConfigurationManager serverConfigurationManager) {
            int pluginPageConfigVersion = 2;
            string pluginPagesConfig = Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.PluginPages", "config.json");

            JsonObject config = new JsonObject();
            if (!File.Exists(pluginPagesConfig)) {
                FileInfo info = new FileInfo(pluginPagesConfig);
                info.Directory?.Create();
            } else {
                config = PluginPagesJson.Parse(File.ReadAllText(pluginPagesConfig));
            }

            if (!config.ContainsKey("pages")) {
                config.Add("pages", new JsonArray());
            }

            if (config["pages"] is not JsonArray pages || pages.Any(page =>
                page is not JsonObject entry ||
                (entry.ContainsKey("Id") && !PluginPagesJson.IsString(entry["Id"], out _)))) {
                throw new JsonException("Plugin Pages configuration contains an invalid pages collection or page ID.");
            }

            JsonObject? hssPageConfig = pages.OfType<JsonObject>().FirstOrDefault(IsOwnPage);

            if (hssPageConfig != null) {
                if (!TryReadVersion(hssPageConfig["Version"], out int? version)) {
                    throw new JsonException("AniDoki's Plugin Pages registration contains an invalid version.");
                }

                if ((version ?? 0) < pluginPageConfigVersion) {
                    pages.Remove(hssPageConfig);
                }
            }

            if (!pages.OfType<JsonObject>().Any(IsOwnPage)) {
                Assembly? pluginPagesAssembly = AssemblyLoadContext.All.SelectMany(x => x.Assemblies).FirstOrDefault(x => x.FullName?.Contains("Jellyfin.Plugin.PluginPages") ?? false);

                Version earliestVersionWithSubUrls = new Version("2.4.1.0");
                bool supportsSubUrls = pluginPagesAssembly != null && pluginPagesAssembly.GetName().Version >= earliestVersionWithSubUrls;

                string rootUrl = serverConfigurationManager.GetNetworkConfiguration().BaseUrl.TrimStart('/').Trim();
                if (!string.IsNullOrEmpty(rootUrl)) {
                    rootUrl = $"/{rootUrl}";
                }

                pages.Add(new JsonObject {
                    ["Id"] = typeof(Plugin).Namespace,
                    ["Url"] = $"{(supportsSubUrls ? "" : rootUrl)}/AniDoki/settings",
                    ["DisplayText"] = "AniDōki",
                    ["Icon"] = "sync",
                    ["Version"] = pluginPageConfigVersion
                });

                File.WriteAllText(pluginPagesConfig, PluginPagesJson.Serialize(config));
            }
        }

        private static bool IsOwnPage(JsonObject page) =>
            PluginPagesJson.IsString(page["Id"], out var id) && id == typeof(Plugin).Namespace;

        // Plugin Pages has stored the version as both a number and a numeric string.
        private static bool TryReadVersion(JsonNode? node, out int? version) {
            version = null;
            if (node == null)
                return true;
            if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out int number)) {
                version = number;
                return true;
            }
            if (PluginPagesJson.IsString(node, out var text) && int.TryParse(text, out number)) {
                version = number;
                return true;
            }
            return false;
        }

        public void RemovePluginPages(IApplicationPaths applicationPaths) {
            string path = Path.Combine(applicationPaths.PluginConfigurationsPath,
                "Jellyfin.Plugin.PluginPages", "config.json");
            PluginPagesCleanup.Remove(path, typeof(Plugin).Namespace!, _logger,
                PluginPagesRuntime.RemovePage);
        }

        public override void OnUninstalling() {
            RemovePluginPages(ApplicationPaths);
            try { Notifications.NotificationWebIntegration.Remove(); } catch (Exception) { _logger.LogWarning("Could not remove AniDoki notification bootstrap on uninstall."); }
            RemoveData();
            base.OnUninstalling();
        }

        private void RemoveData() {
            string directory = Path.GetDirectoryName(Database.FilePath)!;
            try {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                _logger.LogWarning(error, "Could not remove AniDoki data at {DataDirectory} on uninstall", directory);
            }
        }

        public IEnumerable<PluginPageInfo> GetPages() {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    DisplayName = "AniDōki",
                    EnableInMainMenu = true,
                    MenuIcon = "sync",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.ConfigPage.html"
                },
                new PluginPageInfo {
                    Name = "AniDoki_ConfigPageJs",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.ConfigPageJs.js"
                },
                new PluginPageInfo {
                    Name = "AniDoki_CommonJs",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.CommonJs.js"
                },
                new PluginPageInfo {
                    Name = "AniDoki_Styles",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.Anidoki.css"
                },
                new PluginPageInfo {
                    Name = "AniDoki_ConfigStateJs",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.ConfigStateJs.js"
                },
                new PluginPageInfo {
                    Name = "AniDoki_ConfigPageUserJs",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.ConfigPageUserJs.js"
                },
                new PluginPageInfo {
                    Name = "AniDoki_ManualSync",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.ManualSync.html"
                },
                new PluginPageInfo {
                    Name = "AniDoki_ManualSyncJs",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.ManualSyncJs.js"
                }
            };
        }

        /// <summary>
        /// Get the views that the plugin serves.
        /// </summary>
        /// <returns>Array of <see cref="PluginPageInfo"/>.</returns>
        public IEnumerable<PluginPageInfo> GetViews() {
            return new[]
            {
                new PluginPageInfo {
                    Name = "settings",
                    EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.ConfigPageUser.html"
                }
            };
        }
    }
}
