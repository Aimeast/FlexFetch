using FlexFetch.Enums;

namespace FlexFetch.Entities;

/// <summary>
/// A single cookie entry in the centralized cookie pool.
/// </summary>
public sealed class CookieItem
{
    public string Domain { get; set; } = string.Empty;

    public string Path { get; set; } = "/";

    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool Secure { get; set; }

    public bool HttpOnly { get; set; }

    public SameSitePolicy SameSite { get; set; } = SameSitePolicy.Unspecified;

    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Sibling domains (leading-dot form, e.g. ".google.com") that this cookie
    /// is shared to: the same session identity cookie exists on those domains
    /// too, so reads materialize it there and writes update this single entry.
    /// Null or empty means the cookie is not shared across domains.
    /// </summary>
    public List<string>? SharedDomains { get; set; }
}
