using System.Net;
using System.Text.RegularExpressions;

namespace Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

public static partial class SafeUrl
{
    /// <summary>
    ///     Resolves a topic-page link/image against the topic URL and returns a normalized
    ///     absolute http(s) URL, or null for anything unsafe: non-http schemes, user-info,
    ///     or query strings carrying token/auth/session/password/cookie parameters.
    /// </summary>
    public static string? ToSafePublicUrl(string? raw, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            !Uri.TryCreate(new Uri(sourceUrl), WebUtility.HtmlDecode(raw), out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            SensitiveQueryRegex().IsMatch(uri.Query))
            return null;
        return uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    }

    [GeneratedRegex("(?:token|auth|session|password|cookie)=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryRegex();
}
