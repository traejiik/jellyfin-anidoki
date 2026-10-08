using System;
using jellyfin_anidoki.Configuration;

namespace jellyfin_anidoki.Models;

public class StoredState {
    public Guid UserId { get; set; }
    public ApiName ApiName { get; set; }
}