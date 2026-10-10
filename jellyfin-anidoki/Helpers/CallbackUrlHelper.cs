#nullable enable
using System;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;

namespace jellyfin_anidoki.Helpers;

public static class CallbackUrlHelper
{
    public static string Build(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            !uri.IsWellFormedOriginalString() ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Enter an absolute HTTP(S) server address without credentials, a query, or a fragment", nameof(address));
        }

        var builder = new UriBuilder(uri) {
            Path = uri.AbsolutePath.TrimEnd('/') + "/AniDoki/authCallback"
        };
        return builder.Uri.AbsoluteUri;
    }

    public static string GetLocalBaseAddress(IServerApplicationHost serverHost, HttpContext? context)
    {
        var builder = new UriBuilder {
            Scheme = serverHost.ListenWithHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            Host = context?.Connection.LocalIpAddress?.ToString() ?? "localhost",
            Port = serverHost.ListenWithHttps ? serverHost.HttpsPort : serverHost.HttpPort,
            Path = context?.Request.PathBase.Value ?? string.Empty
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}
