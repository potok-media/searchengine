using Microsoft.Extensions.Configuration;

namespace Potok.SearchEngine.Core.Models.Options;

/// <summary>
///     SearchEngine runtime config from config.yml (trackers, cache, proxy, etc.).
///     Listen port is set via the PORT environment variable, not this file.
/// </summary>
public class Config
{
    [ConfigurationKeyName("refresh")]
    public RefreshSettings Refresh { get; set; } = new();

    [ConfigurationKeyName("proxy")]
    public ProxySettings Proxy { get; set; } = new();

    [ConfigurationKeyName("flaresolverr")]
    public FlareSolverrSettings FlareSolverr { get; set; } = new();

    [ConfigurationKeyName("cache")]
    public Cache Cache { get; set; } = new();

    [ConfigurationKeyName("rutracker")]
    public RuTrackerSettings RuTracker { get; set; } = new();

    [ConfigurationKeyName("animelayer")]
    public AnimeLayerSettings AnimeLayer { get; set; } = new();

    [ConfigurationKeyName("nnmclub")]
    public NNMClubSettings NNMClub { get; set; } = new();

    [ConfigurationKeyName("rutor")]
    public RuTorSettings RuTor { get; set; } = new();

    [ConfigurationKeyName("aniliberty")]
    public AnilibertySettings Aniliberty { get; set; } = new();

    [ConfigurationKeyName("kinozal")]
    public KinozalSettings Kinozal { get; set; } = new();

    [ConfigurationKeyName("megapeer")]
    public MegaPeerSettings MegaPeer { get; set; } = new();
}
