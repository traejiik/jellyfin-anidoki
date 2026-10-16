using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using jellyfin_anidoki.Configuration;
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

        public Plugin(IApplicationPaths applicationPaths,
            IServerConfigurationManager serverConfigurationManager,
            IXmlSerializer xmlSerializer,
            ILogger<Plugin> logger) : base(applicationPaths, xmlSerializer) {
            _logger = logger;
            Instance = this;
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
            base.OnUninstalling();
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
