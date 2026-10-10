#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;

namespace jellyfin_anidoki.Notifications;

public sealed record AuthenticatedSessionIdentity(Guid UserId, string SessionId, [property: System.Text.Json.Serialization.JsonIgnore, Newtonsoft.Json.JsonIgnore] string AuthenticationKey);
public sealed record AuthenticatedSessionResolution(AuthenticatedSessionIdentity? Identity, int FailureStatus);

public sealed class AuthenticatedSessionResolver(IAuthorizationContext authorization, ISessionManager sessions, IUserManager users)
{
    public async Task<AuthenticatedSessionResolution> Resolve(HttpContext context)
    {
        var auth = await authorization.GetAuthorizationInfo(context.Request);
        if (auth == null || auth.IsApiKey || auth.UserId == Guid.Empty || string.IsNullOrWhiteSpace(auth.Token) ||
            users.GetUserById(auth.UserId) == null) return new(null, StatusCodes.Status401Unauthorized);
        // The caller's device header must never select or create the token's session.
        var session = await sessions.GetSessionByAuthenticationToken(auth.Token, null!, context.Connection.RemoteIpAddress?.ToString() ?? "");
        if (session == null || session.UserId != auth.UserId || string.IsNullOrWhiteSpace(session.Id) ||
            string.IsNullOrWhiteSpace(auth.DeviceId) || !string.Equals(session.DeviceId, auth.DeviceId, StringComparison.OrdinalIgnoreCase))
            return new(null, StatusCodes.Status403Forbidden);
        // Structured encoding avoids ambiguous concatenations. No token is retained.
        var identityBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { auth.UserId.ToString("N"), session.DeviceId.ToUpperInvariant(), auth.Token }));
        var key = Convert.ToHexString(SHA256.HashData(identityBytes));
        CryptographicOperations.ZeroMemory(identityBytes);
        return new(new(auth.UserId, session.Id, key), 0);
    }
}
