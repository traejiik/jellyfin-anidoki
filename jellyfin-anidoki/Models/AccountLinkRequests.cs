#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Models;

public sealed class PasswordGrantRequest
{
    public ApiName Provider { get; set; }
    public Guid User { get; set; }
    [Required] public string Username { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
}

public sealed class AnnictTokenRequest
{
    [Required] public string Token { get; set; } = string.Empty;
}
