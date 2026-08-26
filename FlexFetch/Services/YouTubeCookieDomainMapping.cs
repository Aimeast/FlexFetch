using FlexFetch.Entities;

namespace FlexFetch.Services;

/// <summary>
/// Dedicated mapping for YouTube/Google sessions: Google identity cookies
/// (SID family) exist with the same value on both .youtube.com and
/// .google.com, so a shared cookie is stored once and materialized to the
/// sibling domain on reads.
/// </summary>
public sealed class YouTubeCookieDomainMapping : ICookieDomainMapping
{
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

    public bool IsMatch(CookieGroup group)
    {
        var name = Normalize(group.Name);
        if (Siblings.ContainsKey(name))
        {
            return true;
        }

        return group.Cookies.Any(c => Siblings.ContainsKey(Normalize(c.Domain)));
    }

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

    private static string Normalize(string? domain) =>
        (domain ?? string.Empty).TrimStart('.').ToLowerInvariant();
}
