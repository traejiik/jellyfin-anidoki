using System;
using System.ComponentModel.DataAnnotations;
using jellyfin_anidoki.Trackers;

namespace jellyfin_anidoki.Auth;

public sealed class PasswordGrantRequest {
    public ApiName Provider { get; set; }
    public Guid User { get; set; }
    [Required] public string Username { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
}

public sealed class AnnictTokenRequest {
    [Required] public string Token { get; set; } = string.Empty;
}
