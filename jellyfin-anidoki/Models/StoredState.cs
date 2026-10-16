using System;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Models;

public class StoredState {
    public Guid UserId { get; set; }
    public ApiName ApiName { get; set; }
    /// <summary>PKCE code verifier issued with this state, for providers that use PKCE.</summary>
    public string? CodeVerifier { get; set; }
}
