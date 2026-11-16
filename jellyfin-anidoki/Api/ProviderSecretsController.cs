using System;
using System.Linq;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Models;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace jellyfin_anidoki.Api;

/// <summary>Reports the storage state and saves or clears the trackers' app secrets.</summary>
[ApiController]
[Route("AniDoki")]
public sealed class ProviderSecretsController : ControllerBase {
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("storage")]
    public ActionResult<StorageState> GetStorageState() {
        var plugin = Plugin.Instance;
        if (plugin == null)
            return Ok(new StorageState(false, "The AniDoki plugin is not loaded."));
        return Ok(new StorageState(plugin.StorageError == null, plugin.StorageError));
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("providerSecrets")]
    public ActionResult<ProviderSecretState[]> GetProviderSecrets() {
        var plugin = Plugin.Instance;
        if (plugin == null)
            return StatusCode(500, "Plugin configuration not loaded");
        lock (plugin.PluginConfiguration) {
            return Ok(Enum.GetValues<ApiName>()
                .Select(provider => new ProviderSecretState(provider.ToString(),
                    !string.IsNullOrEmpty(plugin.PluginConfiguration.ProviderApiAuth?.FirstOrDefault(auth => auth.Name == provider)?.ClientSecret)))
                .ToArray());
        }
    }

    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("providerSecret")]
    public IActionResult SetProviderSecret([FromBody] ProviderSecretRequest? request) {
        if (request == null || !Enum.IsDefined(request.Provider) || (!request.Clear && string.IsNullOrWhiteSpace(request.Secret)))
            return BadRequest("A provider and either a secret or Clear are required");
        var plugin = Plugin.Instance;
        if (plugin == null)
            return StatusCode(500, "Plugin configuration not loaded");
        lock (plugin.PluginConfiguration) {
            var configuration = plugin.PluginConfiguration;
            var auth = configuration.ProviderApiAuth?.FirstOrDefault(item => item.Name == request.Provider);
            bool added = auth == null;
            if (auth == null) {
                auth = new ProviderApiAuth { Name = request.Provider };
                configuration.ProviderApiAuth = [.. configuration.ProviderApiAuth ?? [], auth];
            }
            string? previous = auth.ClientSecret;
            auth.ClientSecret = request.Clear ? null : request.Secret!.Trim();
            try {
                plugin.SaveConfiguration();
            } catch (InvalidOperationException error) {
                auth.ClientSecret = previous;
                if (added)
                    configuration.ProviderApiAuth = (configuration.ProviderApiAuth ?? []).Where(item => item != auth).ToArray();
                return StatusCode(503, error.Message);
            }
            return Ok(new ProviderSecretState(request.Provider.ToString(), !request.Clear));
        }
    }
}
