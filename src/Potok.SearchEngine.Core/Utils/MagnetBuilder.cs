using System.Text;
using System.Text.RegularExpressions;

namespace Potok.SearchEngine.Core.Utils;

/// <summary>
///     Canonical magnet construction and info-hash handling.
///     Replaces the former duplicates: TorrentIdentityResolver.BuildCanonicalMagnet,
///     TorrentCanonicalProjection.BuildMagnet and TorrentMergerService.BuildMagnet.
/// </summary>
public static partial class MagnetBuilder
{
    private const string BtihPrefix = "urn:btih:";
    private const string BtmhV2Prefix = "urn:btmh:1220";

    /// <summary>
    ///     Builds a canonical magnet URI from an info-hash with an optional set of
    ///     tracker announce URLs. <paramref name="sourceMagnets"/> are mined for their
    ///     <c>tr</c> parameters; everything is merged, deduplicated, sorted and stripped
    ///     of tracker URLs carrying sensitive query parameters (passkey and the like).
    ///     Returns null when the hash is not a valid BitTorrent hash.
    /// </summary>
    public static string? Build(
        string? infoHash,
        IEnumerable<string?>? trackerUrls = null,
        IEnumerable<string?>? sourceMagnets = null)
    {
        var hash = NormalizeHash(infoHash);
        if (hash is null)
            return null;

        var trackers = new SortedSet<string>(StringComparer.Ordinal);
        if (trackerUrls is not null)
            foreach (var url in trackerUrls)
                AddSafeTracker(trackers, url);
        if (sourceMagnets is not null)
            foreach (var tracker in sourceMagnets.SelectMany(ExtractTrackers))
                AddSafeTracker(trackers, tracker);

        var result = new StringBuilder(BuildCanonical(hash));
        foreach (var tracker in trackers)
            result.Append("&tr=").Append(Uri.EscapeDataString(tracker));
        return result.ToString();
    }

    /// <summary>
    ///     Canonical magnet for an already normalized hash: v2 (64 hex chars) gets
    ///     <c>urn:btmh:1220</c>, v1 (40 hex chars) gets <c>urn:btih:</c>.
    /// </summary>
    public static string BuildCanonical(string normalizedHash) =>
        normalizedHash.Length == 64
            ? $"magnet:?xt={BtmhV2Prefix}{normalizedHash}"
            : $"magnet:?xt={BtihPrefix}{normalizedHash}";

    /// <summary>
    ///     Normalizes a raw info-hash to lowercase hex. Accepts 40/64-char hex and
    ///     32/52-char base32. Returns null for anything else.
    /// </summary>
    public static string? NormalizeHash(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        if ((value.Length == 40 || value.Length == 64) && value.All(Uri.IsHexDigit))
            return value.ToLowerInvariant();
        if (value.Length is not (32 or 52))
            return null;

        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var character in value.ToUpperInvariant())
        {
            var index = alphabet.IndexOf(character);
            if (index < 0)
                return null;
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits < 8)
                continue;
            bits -= 8;
            bytes.Add((byte)(buffer >> bits));
            buffer &= (1 << bits) - 1;
        }

        return bytes.Count is 20 or 32
            ? Convert.ToHexString(bytes.ToArray()).ToLowerInvariant()
            : null;
    }

    /// <summary>
    ///     Extracts the normalized info-hash from a magnet URI (btih or btmh v2).
    /// </summary>
    public static string? HashFromMagnet(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet) ||
            !Uri.TryCreate(magnet, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "magnet", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var (name, value) in ParseQuery(uri.Query))
        {
            if (!string.Equals(name, "xt", StringComparison.OrdinalIgnoreCase))
                continue;
            if (value.StartsWith(BtihPrefix, StringComparison.OrdinalIgnoreCase))
                return NormalizeHash(value[BtihPrefix.Length..]);
            if (value.StartsWith(BtmhV2Prefix, StringComparison.OrdinalIgnoreCase))
                return NormalizeHash(value[BtmhV2Prefix.Length..]);
        }

        return null;
    }

    /// <summary>
    ///     Tracker announce URLs declared by a magnet's <c>tr</c> parameters,
    ///     excluding URLs with sensitive query parameters.
    /// </summary>
    public static IEnumerable<string> ExtractTrackers(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet) ||
            !Uri.TryCreate(magnet, UriKind.Absolute, out var uri))
            yield break;

        foreach (var (name, value) in ParseQuery(uri.Query))
            if (string.Equals(name, "tr", StringComparison.OrdinalIgnoreCase) && IsSafeTrackerUrl(value))
                yield return value;
    }

    /// <summary>
    ///     Rebuilds a magnet keeping only non-sensitive parameters and safe tracker
    ///     URLs (RedactSensitive pattern: passkey/token/session-like keys are dropped).
    ///     Non-magnet input is returned unchanged.
    /// </summary>
    public static string? Sanitize(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet) ||
            !magnet.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            return magnet;

        var safe = new List<string>();
        foreach (var part in magnet[8..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var rawName = separator < 0 ? part : part[..separator];
            var rawValue = separator < 0 ? string.Empty : part[(separator + 1)..];
            var name = Uri.UnescapeDataString(rawName);
            if (IsSensitiveKey(name))
                continue;

            if (string.Equals(name, "tr", StringComparison.OrdinalIgnoreCase) &&
                !IsSafeTrackerUrl(Uri.UnescapeDataString(rawValue)))
                continue;

            safe.Add(separator < 0 ? rawName : $"{rawName}={rawValue}");
        }

        return $"magnet:?{string.Join('&', safe)}";
    }

    private static void AddSafeTracker(ISet<string> trackers, string? url)
    {
        if (IsSafeTrackerUrl(url))
            trackers.Add(url!.Trim());
    }

    private static bool IsSafeTrackerUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        return !ParseQuery(uri.Query).Any(pair => IsSensitiveKey(pair.Name));
    }

    private static bool IsSensitiveKey(string key) =>
        SensitiveKeyRegex().IsMatch(key);

    private static IEnumerable<(string Name, string Value)> ParseQuery(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
                yield return (parts[0], Uri.UnescapeDataString(parts[1]));
        }
    }

    [GeneratedRegex(@"cookie|authorization|password|парол|login|логин|token|session|passkey|proxy|user",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyRegex();
}
