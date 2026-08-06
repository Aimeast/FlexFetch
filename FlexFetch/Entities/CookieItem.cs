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
}
