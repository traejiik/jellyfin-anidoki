using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using jellyfin_anidoki.Configuration;
using jellyfin_anidoki.Interfaces;
using jellyfin_anidoki.Models;
using jellyfin_anidoki.Resolution;
using jellyfin_anidoki.Trackers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace jellyfin_anidoki.Matching;

/// <summary>A tracker to read the catalogue through.</summary>
/// <param name="Borrowed">True when the requester has no link to this provider and another user's is used (D12); the editor says so.</param>
public sealed record CatalogueTracker(ITrackerProvider Tracker, bool Borrowed);

/// <summary>Which trackers the match API may read through. The boundary between the match API and the network.</summary>
public interface IMatchTrackers {
    /// <summary>Each linked provider and the link's generation (D1).</summary>
    IReadOnlyDictionary<ApiName, Guid> Links(Guid userId);

    /// <summary>For titles, totals and relations, never list state: the requester's own link, else any linked user's.</summary>
    /// <returns>Null when no user on this server can read the provider.</returns>
    CatalogueTracker? ForCatalogue(Guid requester, ApiName provider);

    /// <summary>For list state: only the owner's own link.</summary>
    ITrackerProvider? ForOwner(Guid owner, ApiName provider);
}

internal sealed class MatchTrackers(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IServerApplicationHost serverApplicationHost,
    IHttpContextAccessor httpContextAccessor, IMemoryCache memoryCache) : IMatchTrackers {
    private readonly ILogger<MatchTrackers> _logger = loggerFactory.CreateLogger<MatchTrackers>();

    private static UserConfig[] Users => Plugin.Instance?.PluginConfiguration.UserConfig ?? [];

    public IReadOnlyDictionary<ApiName, Guid> Links(Guid userId) =>
        (Users.FirstOrDefault(config => config.UserId == userId)?.UserApiAuth ?? [])
            .GroupBy(link => link.Name)
            .ToDictionary(group => group.Key, group => group.First().ConnectionGeneration);

    public CatalogueTracker? ForCatalogue(Guid requester, ApiName provider) {
        if (ForOwner(requester, provider) is { } own)
            return new CatalogueTracker(own, false);
        foreach (UserConfig other in Users.Where(config => config.UserId != requester)) {
            if (Build(other, provider) is { } borrowed)
                return new CatalogueTracker(borrowed, true);
        }
        return null;
    }

    public ITrackerProvider? ForOwner(Guid owner, ApiName provider) =>
        Users.FirstOrDefault(config => config.UserId == owner) is { } config ? Build(config, provider) : null;

    private ITrackerProvider? Build(UserConfig config, ApiName provider) {
        if (config.UserApiAuth?.Any(link => link.Name == provider) != true)
            return null;
        var clients = new TrackerClientFactory(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor, memoryCache, new Delayer(), _logger);
        return clients.Create(provider, config) is { } client ? clients.Adapt(provider, client) : null;
    }
}

/// <summary>What automatic resolution would save as a match. The boundary to the anime-list and offline-database downloads.</summary>
public interface IAutomaticMatches {
    Task<DerivedSegmentation> Derive(LibrarySource source, ITrackerProvider tracker, CancellationToken cancellationToken);
}

internal sealed class AutomaticMatches(IHttpClientFactory httpClientFactory, IApplicationPaths applicationPaths, ILoggerFactory loggerFactory) : IAutomaticMatches {
    private readonly ILogger<AutomaticMatches> _logger = loggerFactory.CreateLogger<AutomaticMatches>();

    /// <summary>Resolves the highest episode, so a walk covers every part the season holds, and stops the match there (PR 9, Differences item 11).</summary>
    public async Task<DerivedSegmentation> Derive(LibrarySource source, ITrackerProvider tracker, CancellationToken cancellationToken) {
        SourceIds ids = await new SourceIdResolver(httpClientFactory, applicationPaths, loggerFactory, _logger).Resolve(source.Last);
        SourceResolution answer = await new AutomaticResolver(_logger).Resolve(source.Last, ids, tracker, cancellationToken);
        return await new RelationChainSegmenter(tracker, _logger)
            .ToMatch(new MatchScope(tracker.Name, source.Info.SourceKey), source.Last, answer, source.Episodes[^1], cancellationToken);
    }
}
