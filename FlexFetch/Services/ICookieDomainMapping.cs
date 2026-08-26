using FlexFetch.Entities;

namespace FlexFetch.Services;

/// <summary>
/// Site-specific cross-domain cookie sharing: decides whether a cookie on a
/// group's primary domain is shared to sibling domains (e.g. Google identity
/// cookies live on both .youtube.com and .google.com). The default mapping
/// shares nothing.
/// </summary>
public interface ICookieDomainMapping
{
    /// <summary>True when this mapping applies to the given group.</summary>
    bool IsMatch(CookieGroup group);

    /// <summary>
    /// Sibling domains (leading-dot form) a cookie with the given primary
    /// domain and name is shared to, or an empty list when not shared.
    /// </summary>
    IReadOnlyList<string> GetSharedDomains(string? domain, string name);
}
