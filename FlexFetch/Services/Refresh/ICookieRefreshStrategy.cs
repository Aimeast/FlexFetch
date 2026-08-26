using FlexFetch.Entities;

namespace FlexFetch.Services.Refresh;

/// <summary>
/// Signals extracted from a refresh visit, used to detect a flagged session
/// that a landing-URL check would miss (e.g. a bot-check interstitial served
/// in-page, or an explicit logged-out state).
/// </summary>
public sealed record CookieRefreshPageSignals(
    string? PageText,
    bool? LoggedIn,
    bool ApiAuthFailed,
    bool SignInButtonPresent);

/// <summary>
/// Site-specific behavior for the cookie refresh pipeline. A strategy
/// isolates the checks that only apply to one site (e.g. YouTube/Google
/// session rejection) from the generic refresh flow, so other sites can be
/// refreshed with the default behavior and future sites can add their own
/// strategy without touching the pipeline.
/// </summary>
public interface ICookieRefreshStrategy
{
    /// <summary>True when this strategy applies to the given group.</summary>
    bool IsMatch(CookieGroup group);

    /// <summary>
    /// Cookie domains whose cookies must also be present in the browser for
    /// this group's session to be recognized (e.g. Google identity cookies
    /// live on .google.com for a YouTube session). The refresh pushes these
    /// domains' cookies into the browser alongside the group's own and
    /// exports them back afterwards. Empty for sites with no related domain.
    /// </summary>
    IReadOnlyList<string> GetRelatedCookieDomains(CookieGroup group);

    /// <summary>
    /// Returns a failure reason when the page the browser landed on after a
    /// visit indicates the presented session was not accepted (e.g. a login
    /// or verification page); null when the page is fine. A null or empty URL
    /// is never a rejection.
    /// </summary>
    string? GetSessionRejectionReason(string? pageUrl);

    /// <summary>
    /// Returns a failure reason when signals extracted from the visited page
    /// (visible text, reported login state, API auth failures) indicate the
    /// presented session was flagged (e.g. a bot-check interstitial); null
    /// when the page looks healthy.
    /// </summary>
    string? GetPageContentRejectionReason(CookieRefreshPageSignals signals);

    /// <summary>
    /// Returns a failure reason when the cookies exported from the browser
    /// show the session was rejected (e.g. all identity cookies vanished);
    /// null when the export looks healthy.
    /// </summary>
    string? GetSessionRejectionReason(IReadOnlyList<CookieItem> before, IReadOnlyList<CookieItem> exported);
}
