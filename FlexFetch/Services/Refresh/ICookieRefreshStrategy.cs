using FlexFetch.Entities;

namespace FlexFetch.Services.Refresh;

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
    /// Returns a failure reason when the page the browser landed on after a
    /// visit indicates the presented session was not accepted (e.g. a login
    /// or verification page); null when the page is fine. A null or empty URL
    /// is never a rejection.
    /// </summary>
    string? GetSessionRejectionReason(string? pageUrl);

    /// <summary>
    /// Returns a failure reason when the cookies exported from the browser
    /// show the session was rejected (e.g. all identity cookies vanished);
    /// null when the export looks healthy.
    /// </summary>
    string? GetSessionRejectionReason(IReadOnlyList<CookieItem> before, IReadOnlyList<CookieItem> exported);
}
