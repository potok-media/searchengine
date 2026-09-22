using Microsoft.Extensions.Configuration;

namespace Potok.SearchEngine.Core.Models.Options;

/// <summary>
///     Настройки внешнего резолвера медиа-имён (alloha.tv).
/// </summary>
public class MediaResolverSettings
{
    /// <summary>
    ///     Токен alloha.tv. Если не задан, внешний резолв имён по kp/tt/tmdb id отключён.
    /// </summary>
    [ConfigurationKeyName("alloha-token")]
    public string? AllohaToken { get; set; }
}
