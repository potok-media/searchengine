namespace Potok.SearchEngine.Infrastructure.Trackers.RuTracker;

/// <summary>
///     RuTracker forum-category map: forum id → normalized types + title parser flavor.
/// </summary>
public partial class BaseRuTracker
{
    protected static readonly IReadOnlyDictionary<string, CategoryInfo> CategoryMap = BuildCategoryMap();

    private static IReadOnlyDictionary<string, CategoryInfo> BuildCategoryMap()
    {
        var map = new Dictionary<string, CategoryInfo>(StringComparer.OrdinalIgnoreCase);

        Add(map, CategoryParser.Movie, ["movie"],
            "549", "22", "1666", "941", "1950", "2090", "2221", "2091", "2092", "2093", "2200",
            "2540", "934", "505", "124", "1457", "2199", "313", "312", "1247", "2201", "2339", "140",
            "252", "718", "2198");

        Add(map, CategoryParser.Movie, ["multfilm"], "2343", "930", "2365", "208", "539", "209", "1213");
        Add(map, CategoryParser.Serial, ["multserial"], "921", "815", "1460");

        Add(map, CategoryParser.Serial, ["serial"],
            "842", "235", "242", "819", "1531", "721", "1102", "1120", "1214", "489", "387", "9", "81",
            "119", "1803", "266", "193", "1690", "1459", "825", "1248", "1288", "325", "534", "694",
            "704", "915", "1939");

        Add(map, CategoryParser.Generic, ["anime"], "1105", "1106", "2491", "1389");
        Add(map, CategoryParser.Movie, ["documovie"], "709", "2109");
        Add(map, CategoryParser.Generic, ["docuserial", "documovie"],
            "46", "671", "2177", "2538", "251", "98", "97", "851", "2178", "821", "2076", "56", "2123",
            "876", "2139", "1467", "1469", "249", "552", "500", "2112", "1327", "1468", "2168", "2160",
            "314", "1281", "2110", "979", "2169", "2164", "2166", "2163");
        Add(map, CategoryParser.Generic, ["tvshow"], "24", "1959", "939", "1481", "113", "115", "882",
            "1482", "393", "2537", "532", "827");
        Add(map, CategoryParser.Generic, ["sport"],
            "2103", "2522", "2485", "2486", "2479", "2089", "1794", "845", "2312", "343", "2111",
            "1527", "2069", "1323", "2009", "2000", "2010", "2006", "2007", "2005", "259", "2004",
            "1999", "2001", "2002", "283", "1997", "2003", "1608", "1609", "2294", "1229", "1693",
            "2532", "136", "592", "2533", "1952", "1621", "2075", "1668", "1613", "1614", "1623",
            "1615", "1630", "2425", "2514", "1616", "2014", "1442", "1491", "1987", "1617", "1620",
            "1998", "1343", "751", "1697", "255", "260", "261", "256", "1986", "660", "1551", "626",
            "262", "1326", "978", "1287", "1188", "1667", "1675", "257", "875", "263", "2073", "550",
            "2124", "1470", "528", "486", "854", "2079", "1336", "2171", "1339", "2455", "1434",
            "2350", "1472", "2068", "2016");

        return map;
    }

    private static void Add(Dictionary<string, CategoryInfo> map, CategoryParser parser, string[] types,
        params string[] categories)
    {
        foreach (var category in categories)
            map[category] = new CategoryInfo(types, parser);
    }

    private static bool TryGetCategory(string categoryId, out CategoryInfo category)
    {
        return CategoryMap.TryGetValue(categoryId, out category!);
    }

    protected enum CategoryParser
    {
        Default,
        Movie,
        Serial,
        Generic
    }

    protected sealed record CategoryInfo(string[] Types, CategoryParser Parser);
}
