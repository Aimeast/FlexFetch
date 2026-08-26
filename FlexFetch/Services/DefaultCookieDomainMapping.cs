using FlexFetch.Entities;

namespace FlexFetch.Services;

/// <summary>
/// Fallback mapping: no cross-domain sharing, every cookie stays on its own
/// domain. Never matches, so it is only used as the default when no
/// site-specific mapping applies.
/// </summary>
public sealed class DefaultCookieDomainMapping : ICookieDomainMapping
{
    public bool IsMatch(CookieGroup group) => false;

    public IReadOnlyList<string> GetSharedDomains(string? domain, string name) =>
        Array.Empty<string>();
}
