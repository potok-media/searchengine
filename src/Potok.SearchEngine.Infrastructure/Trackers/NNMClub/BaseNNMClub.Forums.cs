namespace Potok.SearchEngine.Infrastructure.Trackers.NNMClub;

/// <summary>
///     Explicit allowlist captured from the public tracker.php forum taxonomy. Parent and
///     ambiguous non-video forums are deliberately excluded; no release-title inference is used.
/// </summary>
public partial class BaseNNMClub
{
    private static readonly IReadOnlyDictionary<string, string[]> ForumTypeMap = BuildForumTypeMap();

    private static IReadOnlyDictionary<string, string[]> BuildForumTypeMap()
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        Add(["724"], ["movie", "serial", "multfilm", "multserial"]);
        Add(["725", "729"], ["documovie", "docuserial"]);
        Add(["731", "1345", "733", "1346"], ["movie", "serial"]);
        Add(["1329", "1330", "1331", "1332", "1340", "890", "1336", "1337", "1338", "1339", "660"],
            ["multfilm"]);
        Add(["658", "232"], ["multserial"]);

        Add([
            "216", "270", "218", "219", "954", "217", "1293", "1298", "318", "320", "677",
            "1177", "319", "678", "885", "908", "1310", "909", "910", "911", "912", "220", "221",
            "222", "882", "889", "224", "225", "226", "227", "1296", "891", "1299", "682", "694",
            "884", "1211", "693", "913", "228", "1150", "1311", "1313", "1312", "1294", "668"
        ], ["movie"]);

        Add([
            "1219", "1221", "1220", "722", "768", "1344", "779", "1288", "787", "1141", "777",
            "786", "776", "785", "775", "1265", "1242", "1140", "782", "773", "1142", "772", "771",
            "783", "1144", "804", "1290", "1300", "784", "774", "922", "770", "1320", "780", "781",
            "1322", "769", "799", "800", "791", "793", "794", "796", "795", "802"
        ], ["serial"]);

        Add([
            "713", "706", "577", "894", "578", "580", "579", "953", "581", "806", "714", "761",
            "809", "924", "812", "576", "590", "591", "588", "589", "598", "652", "597", "593", "594",
            "819", "595", "587", "584", "586", "585", "600", "596", "614", "669"
        ], ["documovie", "docuserial"]);
        Add(["599", "959", "956", "1295", "610", "613", "612", "653", "654", "611", "656", "400"],
            ["tvshow"]);
        Add([
            "603", "1308", "1309", "1206", "1194", "1062", "974", "609", "1263", "951", "975", "608",
            "607", "606", "750", "605", "604", "950"
        ], ["sport"]);

        Add([
            "620", "623", "622", "621", "632", "624", "627", "626", "625", "644", "628", "635", "634",
            "638", "646", "169"
        ], ["anime"]);
        return result;

        void Add(IEnumerable<string> ids, string[] types)
        {
            foreach (var id in ids)
                result[id] = types;
        }
    }
}
