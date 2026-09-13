using FlexFetch.Enums;

namespace FlexFetch.Entities;

/// <summary>
/// A single cookie entry, stored in the session snapshot and handed to
/// yt-dlp as a Netscape jar. A domain WITHOUT a leading dot is host-only
/// (must never be widened to subdomains); a leading dot means the cookie
/// applies to the domain and all its subdomains.
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
}
