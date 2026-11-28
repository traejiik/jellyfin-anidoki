using System.ComponentModel.DataAnnotations;

namespace jellyfin_anidoki.Configuration;

public enum ApiName {
    [Display(Name = "MyAnimeList")]
    Mal,
    [Display(Name = "AniList")]
    AniList,
    [Display(Name = "Kitsu")]
    Kitsu,
    [Display(Name = "Annict")]
    Annict,
    [Display(Name = "Shikimori")]
    Shikimori,
    [Display(Name = "Simkl")]
    Simkl
}
