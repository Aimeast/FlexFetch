using FlexFetch.Entities;

namespace FlexFetch.Services.Refresh;

/// <summary>
/// Refresh behavior specific to YouTube/Google cookie groups. YouTube stores
/// its session identity in Google root-domain cookies (SID/APISID/LOGIN_INFO
/// on .google.com), so a refresh must detect when the presented session was
/// rejected: landing on a Google login/verification page, or the identity
/// cookies vanishing from the browser after the visit. In both cases the
/// refresh cannot renew the session and must fail honestly so the pool keeps
/// its old cookies.
/// </summary>
public sealed class YouTubeCookieRefreshStrategy : ICookieRefreshStrategy
{
    /// <summary>Matches groups whose URLs or cookie domains belong to YouTube.</summary>
    public bool IsMatch(CookieGroup group)
    {
        return group.Urls.Any(IsYouTubeUrl)
            || group.Cookies.Any(c => IsYouTubeHost(c.Domain));
    }

    /// <summary>
    /// Google identity cookies live on the .google.com root domain, so a
    /// YouTube refresh must push and export them alongside the group's own.
    /// </summary>
    public IReadOnlyList<string> GetRelatedCookieDomains(CookieGroup group) => new[] { "google.com" };

    public string? GetSessionRejectionReason(string? pageUrl) =>
        IsLoginRedirectUrl(pageUrl)
            ? "Session not recognized by YouTube (logged in=false); re-export cookies from a real browser"
            : null;

    /// <summary>
    /// Bot-check interstitials are served in-page (the URL stays on
    /// youtube.com), so the landing-URL check cannot see them. Detect the
    /// flag from the visible page text, YouTube's reported login state and
    /// InnerTube API auth failures instead.
    /// </summary>
    public string? GetPageContentRejectionReason(CookieRefreshPageSignals signals)
    {
        if (signals.LoggedIn == false)
        {
            return "Session not recognized by YouTube (logged in=false); re-export cookies from a real browser";
        }

        if (signals.SignInButtonPresent)
        {
            return "YouTube shows a Sign in button (session logged out); re-export cookies from a real browser";
        }

        if (signals.ApiAuthFailed)
        {
            return "YouTube InnerTube API rejected the session (401/403); re-export cookies from a real browser";
        }

        if (!string.IsNullOrWhiteSpace(signals.PageText)
            && BotMarkers.Any(m => signals.PageText.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return "YouTube bot-check page detected; session flagged; re-export cookies from a real browser";
        }

        return null;
    }

    /// <summary>Text markers that identify YouTube's in-page bot-check interstitial.</summary>
    private static readonly string[] BotMarkers =
    {
        "confirm you're not a bot",
        "Sign in to confirm",
        "verify you're human",
        "unusual traffic",
        "Enable cookies and reload the page",
    };

    public string? GetSessionRejectionReason(IReadOnlyList<CookieItem> before, IReadOnlyList<CookieItem> exported)
    {
        var identityBefore = before.Where(c => IsIdentityCookieName(c.Name)).ToList();
        var identityAfter = exported.Where(c => IsIdentityCookieName(c.Name)).ToList();
        return identityBefore.Count > 0 && identityAfter.Count == 0
            ? "Session cookies removed by site; re-export cookies from a real browser"
            : null;
    }

    /// <summary>True when the URL (or a bare host/domain) belongs to YouTube.</summary>
    private static bool IsYouTubeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? IsYouTubeHost(uri.Host) : IsYouTubeHost(url);

    /// <summary>True when the host (or a leading-dot domain) belongs to YouTube.</summary>
    private static bool IsYouTubeHost(string? host)
    {
        var h = host?.TrimStart('.');
        return !string.IsNullOrWhiteSpace(h)
            && (h.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
                || h.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the landing URL indicates a login or verification page
    /// (accounts.google.com, Google servicelogin, /sorry challenge, generic
    /// signin/login paths), meaning the presented session was not accepted.
    /// </summary>
    public static bool IsLoginRedirectUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("servicelogin", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/sorry/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("signin", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for cookies that carry the session identity (auth/session tokens).
    /// If these all vanish from the browser after a refresh visit, the site
    /// rejected the presented session (flagged), and the refresh cannot
    /// renew it - the pool should keep its old cookies instead of syncing
    /// the deletions.
    /// </summary>
    private static bool IsIdentityCookieName(string name) =>
        name.Contains("SID", StringComparison.OrdinalIgnoreCase)
        || name.Contains("APISID", StringComparison.OrdinalIgnoreCase)
        || name.Contains("LOGIN_INFO", StringComparison.OrdinalIgnoreCase);
}
