using Microsoft.Extensions.Configuration;

namespace Potok.SearchEngine.Core.Models.Options.TrackerConfigs;

public class RuTrackerSettings : BaseTrackerConfig
{
    /// <summary>
    ///     Данные для авторизации на трекере.
    /// </summary>
    [ConfigurationKeyName("authorization")]
    public Authorization Authorization { get; set; } = new();
}
