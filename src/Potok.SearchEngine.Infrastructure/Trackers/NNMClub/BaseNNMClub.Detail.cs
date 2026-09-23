using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.NNMClub;

/// <summary>
///     NNMClub detail-side helpers: explicit-hash extraction from the topic page,
///     safe-URL normalization with optional query stripping, and page classification probes.
///     Magnet/explicit-hash identity resolution is the shared
///     <see cref="TorrentIdentity"/> in Core.
/// </summary>
public partial class BaseNNMClub
{
    private static string? ExtractExplicitHash(IDocument document)
    {
        var fromAttribute = document.QuerySelector("[data-hash]")?.GetAttribute("data-hash");
        if (!string.IsNullOrWhiteSpace(fromAttribute))
            return fromAttribute.Trim();

        var match = Regex.Match(
            document.QuerySelector(".postbody, .post_body, #tor-reged")?.TextContent ?? string.Empty,
            @"(?:инфо[ -]?хеш|info[ -]?hash|хеш)\s*:?[\s\[]*(?<hash>[a-f0-9]{40}|[a-f0-9]{64}|[a-z2-7]{32})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["hash"].Value : null;
    }

    internal static bool IsSearchSurface(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;
        var document = new HtmlParser().ParseDocument(html);
        return document.QuerySelector("form[action*='tracker.php'], a[href*='tracker.php?c='], tr.prow1, tr.prow2")
               is not null;
    }

    internal static bool IsAuthenticationResponse(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return false;
        return IsAuthenticationPage(new HtmlParser().ParseDocument(html));
    }

    private static bool IsAuthenticationPage(IDocument document)
    {
        if (document.QuerySelector("form[action*='login.php'] input[name='username'], input[name='password']")
            is not null)
            return true;
        var text = TrackerText.NormalizeText(document.Body?.TextContent ?? string.Empty);
        return text.Contains("необходимо авторизоваться", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("требуется авторизация", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("введите имя пользователя", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ToSafePublicUrl(string? value, string baseUrl, bool stripQuery)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(new Uri(baseUrl), WebUtility.HtmlDecode(value), out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrWhiteSpace(uri.UserInfo))
            return null;
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        if (stripQuery || Regex.IsMatch(uri.Query, @"(?:token|auth|password|cookie|session)=",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            builder.Query = string.Empty;
        return builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
    }

    private static bool IsNnmClubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Host, new Uri("https://nnmclub.to").Host, StringComparison.OrdinalIgnoreCase);

    private static string? MatchQueryId(string? href, string name)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var match = Regex.Match(WebUtility.HtmlDecode(href), $@"(?:[?&]){Regex.Escape(name)}=(?<id>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static int ParseInteger(string? value) => ParseNullableInteger(value) ?? 0;

    private static int? ParseNullableInteger(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, @"\d+");
        return match.Success && int.TryParse(match.Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string? NormalizeAuthor(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, @"(?:^|\s)by:\s*(?<author>.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? NullIfEmpty(TrackerText.NormalizeText(match.Groups["author"].Value)) : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
