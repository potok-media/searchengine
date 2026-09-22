using Microsoft.Extensions.Configuration;

namespace Potok.SearchEngine.Core.Models.Options.TrackerConfigs;

public class AnimeLayerSettings : BaseTrackerConfig
{
    [ConfigurationKeyName("authorization")]
    public Authorization Authorization { get; set; } = new();
}
