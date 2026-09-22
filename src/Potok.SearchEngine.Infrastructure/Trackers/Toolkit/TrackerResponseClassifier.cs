namespace Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

/// <summary>
///     Classifies raw tracker HTML responses into <see cref="TrackerSearchErrorCode"/>:
///     Cloudflare-style challenges, authentication pages and empty/invalid bodies.
///     Tracker-specific contract checks (missing result table etc.) stay in the adapters.
/// </summary>
public static class TrackerResponseClassifier
{
    /// <summary>
    ///     Returns null when the response looks usable, otherwise the mapped error code.
    ///     Empty/whitespace → InvalidResponse; challenge markers → Challenge;
    ///     authentication markers → Authentication.
    /// </summary>
    public static TrackerSearchErrorCode? Classify(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return TrackerSearchErrorCode.InvalidResponse;
        if (IsChallenge(html))
            return TrackerSearchErrorCode.Challenge;
        if (IsAuthentication(html))
            return TrackerSearchErrorCode.Authentication;
        return null;
    }

    /// <summary>Cloudflare / DDoS-Guard interstitial markers.</summary>
    public static bool IsChallenge(string html) =>
        html.Contains("cf-chl-", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("cf_chl", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("ddos-guard", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Full-page authentication responses. Conservative on purpose: in-page login forms
    ///     are common on valid result pages, so only explicit markers count — the ASP.NET
    ///     {"message":"Unauthenticated."} body and dedicated login pages. Adapters with a
    ///     signed-in marker (e.g. RuTracker's "logged-in-username") keep their own check.
    /// </summary>
    public static bool IsAuthentication(string html) =>
        html.Contains("\"Unauthenticated.\"", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("name=\"login_username\"", StringComparison.OrdinalIgnoreCase);
}
