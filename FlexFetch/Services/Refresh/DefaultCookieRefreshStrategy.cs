using FlexFetch.Entities;

namespace FlexFetch.Services.Refresh;

/// <summary>
/// Default refresh behavior for sites without a dedicated strategy: no
/// session-rejection checks. The generic pipeline (visit, export, upsert,
/// sync-delete) applies unchanged.
/// </summary>
public sealed class DefaultCookieRefreshStrategy : ICookieRefreshStrategy
{
    /// <summary>Never matches: it is the fallback when no other strategy applies.</summary>
    public bool IsMatch(CookieGroup group) => false;

    public string? GetSessionRejectionReason(string? pageUrl) => null;

    public string? GetSessionRejectionReason(IReadOnlyList<CookieItem> before, IReadOnlyList<CookieItem> exported) => null;
}
