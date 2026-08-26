using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Refresh;
using FlexFetch.Services.Routing;
using Serilog;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// YouTube downloader: yt-dlp with YouTube-specific URL matching. Also
/// carries the site's cookie refresh strategy and cross-domain mapping
/// (Google identity cookies live on .google.com for a YouTube session), so
/// all YouTube-specific behavior lives with the downloader plugin.
/// Reuses the shared yt-dlp logic from <see cref="YtdlpDownloader"/>.
/// </summary>
public sealed class YouTubeDownloader : YtdlpDownloader, ICookieRefreshStrategy, ICookieDomainMapping
{
    public YouTubeDownloader(
        YtdlpService ytdlp,
        IProxyService proxy,
        StorageService storage,
        ILogger log,
        CookiePoolService cookies,
        Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>>? fetchData = null,
        Func<string, OptionSet, Action<double>, CancellationToken, Task<RunResult<string>>>? download = null)
        : base(ytdlp, proxy, storage, log, cookies, fetchData, download)
    {
    }

    public override string Type => "YouTube";

    public override bool IsDomainSpecific => true;

    /// <summary>YouTube enables the cookie attach-and-retry on auth-class errors.</summary>
    protected override bool AuthRetryEnabled => true;

    /// <summary>
    /// Builds the yt-dlp options and pins resilient YouTube player clients.
    /// A plain request from a datacenter IP is often answered with a bot
    /// challenge ("The page needs to be reloaded" / "Sign in to confirm you're
    /// not a bot"). web_safari additionally demands a po_token and fails with
    /// "The page needs to be reloaded" without one, so only clients that do
    /// not require a po_token are used (android, then tv, then mweb); the
    /// cookie attach-and-retry flow is unchanged.
    /// </summary>
    public override OptionSet BuildOptions(Uri url, string? outputPath = null, string? cookieFile = null)
    {
        var options = base.BuildOptions(url, outputPath, cookieFile);
        // Comma-separated list: yt-dlp picks the first client that works.
        // web_safari forces a po_token (Proof-of-Origin) challenge ("The page needs
        // to be reloaded") when the token is absent; android/tv/mweb do not require it
        // and bypass the bot check once cookies are attached.
        options.ExtractorArgs = "youtube:player_client=android,tv,mweb";
        return options;
    }

    public override bool CanHandle(string url) =>
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

    // --- ICookieRefreshStrategy / ICookieDomainMapping (merged) ---

    /// <summary>
    /// Matches groups whose URL, cookie domain or name belongs to YouTube or
    /// Google (Google identity cookies live on the .google.com root domain).
    /// </summary>
    public bool IsMatch(CookieGroup group)
    {
        var name = Normalize(group.Name);
        return Siblings.ContainsKey(name)
            || group.Urls.Any(IsYouTubeUrl)
            || group.Cookies.Any(c => IsYouTubeHost(c.Domain) || Siblings.ContainsKey(Normalize(c.Domain)));
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

    /// <summary>
    /// Google identity/session cookies that a real browser keeps on both
    /// .youtube.com and .google.com. Everything else (PREF, VISITOR_INFO1_LIVE,
    /// GPS, ...) stays on its own domain.
    /// </summary>
    private static readonly HashSet<string> SharedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SID", "HSID", "SSID", "APISID", "SAPISID", "LOGIN_INFO", "SIDCC", "NID",
        "__Secure-1PSID", "__Secure-1PSIDTS", "__Secure-1PSIDCC", "__Secure-1PSIDRTS",
        "__Secure-3PSID", "__Secure-3PSIDTS", "__Secure-3PSIDCC", "__Secure-3PSIDRTS",
        "__Secure-1PAPISID", "__Secure-3PAPISID",
    };

    /// <summary>
    /// Sibling domains that share the same identity cookies, keyed by the
    /// normalized primary domain (no leading dot, lowercase).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Siblings =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["youtube.com"] = new[] { "google.com" },
            ["google.com"] = new[] { "youtube.com" },
        };

    public IReadOnlyList<string> GetSharedDomains(string? domain, string name)
    {
        var normalized = Normalize(domain);
        if (string.IsNullOrEmpty(normalized) || !SharedNames.Contains(name))
        {
            return Array.Empty<string>();
        }

        return Siblings.TryGetValue(normalized, out var siblings)
            ? siblings.Select(s => "." + s).ToArray()
            : Array.Empty<string>();
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

    private static string Normalize(string? domain) =>
        (domain ?? string.Empty).TrimStart('.').ToLowerInvariant();
}
