using System.Text.Json;
using System.Text.RegularExpressions;
using Potok.SearchEngine.Core.Utils;

namespace Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

/// <summary>
///     Builds and updates the persisted source-payload envelope
///     ({schemaVersion, parserVersion, list, details, unmapped, warnings}) and keeps secrets
///     out of it: magnet strings are rebuilt via <see cref="MagnetBuilder.Sanitize"/>, URL
///     query parameters with sensitive keys are stripped, and dictionary entries with
///     sensitive keys are nulled.
/// </summary>
public static partial class TrackerPayload
{
    public const int SchemaVersion = 1;

    public static readonly IReadOnlyDictionary<string, object?> EmptyObject =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Serializes the payload envelope; list/details/unmapped are sanitized first.</summary>
    public static JsonElement Create(
        string parserVersion,
        object? list,
        object? details,
        object? unmapped,
        IReadOnlyCollection<string> warnings) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = SchemaVersion,
            ["parserVersion"] = parserVersion,
            ["list"] = Sanitize(list),
            ["details"] = Sanitize(details),
            ["unmapped"] = Sanitize(unmapped),
            ["warnings"] = warnings
        });

    /// <summary>Merges a new unmapped fragment into an existing payload's "unmapped" object.</summary>
    public static JsonElement MergeUnmapped(JsonElement payload, object? incoming)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (payload.TryGetProperty("unmapped", out var existing) && existing.ValueKind == JsonValueKind.Object)
            foreach (var property in existing.EnumerateObject())
                merged[property.Name] = property.Value.Clone();

        var incomingElement = JsonSerializer.SerializeToElement(Sanitize(incoming));
        if (incomingElement.ValueKind == JsonValueKind.Object)
            foreach (var property in incomingElement.EnumerateObject())
                merged[property.Name] = property.Value.Clone();
        return JsonSerializer.SerializeToElement(merged);
    }

    /// <summary>
    ///     Replaces <see cref="TorrentDetails.Source"/> after a detail fetch: keeps the stored
    ///     list fragment, swaps in details/unmapped, unions warnings, and updates state,
    ///     observed fields and fetch timestamp. Throws when the item has no source snapshot.
    /// </summary>
    public static TorrentSourceSnapshot WithDetails(
        TorrentDetails torrent,
        string parserVersion,
        TrackerDetailsState state,
        object? details,
        object? unmapped,
        IReadOnlyCollection<string> warnings,
        DateTimeOffset? detailsFetchedAt,
        TorrentObservedFields? observedFields = null)
    {
        var source = torrent.Source
                     ?? throw new InvalidOperationException("List item has no source snapshot to update.");
        var list = source.SourcePayload.TryGetProperty("list", out var listElement)
            ? listElement.Clone()
            : JsonSerializer.SerializeToElement(EmptyObject);
        var existingWarnings = source.SourcePayload.TryGetProperty("warnings", out var warningsElement) &&
                               warningsElement.ValueKind == JsonValueKind.Array
            ? warningsElement.EnumerateArray()
                .Select(value => value.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
            : [];
        torrent.Source = source with
        {
            ObservedFields = observedFields ?? source.ObservedFields,
            DetailsState = state,
            DetailsFetchedAt = detailsFetchedAt,
            SourcePayload = Create(
                parserVersion,
                list,
                details,
                MergeUnmapped(source.SourcePayload, unmapped),
                existingWarnings.Concat(warnings).Distinct(StringComparer.Ordinal).ToArray())
        };
        return torrent.Source;
    }

    /// <summary>
    ///     Deep sanitization for payload fragments: magnets are rebuilt without sensitive
    ///     parameters, sensitive query parameters are stripped from absolute http(s) URLs,
    ///     and dictionary entries with sensitive keys are nulled.
    /// </summary>
    public static object? Sanitize(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return SanitizeString(text);
            case IDictionary<string, object?> dictionary:
            {
                var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, item) in dictionary)
                    copy[key] = IsSensitiveKey(key) ? null : Sanitize(item);
                return copy;
            }
            case System.Collections.IDictionary dictionary:
            {
                var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key)
                        continue;
                    copy[key] = IsSensitiveKey(key) ? null : Sanitize(entry.Value);
                }
                return copy;
            }
            case System.Collections.IEnumerable items:
            {
                var list = new List<object?>();
                foreach (var item in items)
                    list.Add(Sanitize(item));
                return list;
            }
            default:
                return value;
        }
    }

    private static string SanitizeString(string value)
    {
        if (value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return MagnetBuilder.Sanitize(value) ?? value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Query))
            return value;

        var kept = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !IsSensitiveKey(pair.Split('=', 2)[0]))
            .ToArray();
        if (kept.Length == uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Length)
            return value;

        var builder = new UriBuilder(uri) { Query = string.Join('&', kept) };
        return builder.Uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    }

    private static bool IsSensitiveKey(string key) => SensitiveKeyRegex().IsMatch(key);

    [GeneratedRegex(@"cookie|authorization|password|парол|login|логин|token|session|passkey|proxy|user",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyRegex();
}
