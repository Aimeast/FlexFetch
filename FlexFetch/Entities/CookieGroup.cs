using FlexFetch.Entities;
using FlexFetch.Enums;

namespace FlexFetch.Entities;

/// <summary>
/// A group of sites plus their associated cookies, used for grouped refresh.
/// </summary>
public sealed class CookieGroup
{
    public string Id { get; set; } = RandomId.New();

    public string Name { get; set; } = string.Empty;

    /// <summary>Site URLs belonging to this group (e.g. "https://www.youtube.com").</summary>
    public List<string> Urls { get; set; } = new();

    /// <summary>Cookie collection of this group.</summary>
    public List<CookieItem> Cookies { get; set; } = new();

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>State of the latest browser refresh cycle.</summary>
    public CookieRefreshStatus LastRefreshStatus { get; set; } = CookieRefreshStatus.Never;

    /// <summary>When the last successful refresh completed, or null.</summary>
    public DateTime? LastRefreshedAt { get; set; }

    /// <summary>Error message of the last failed refresh, or null.</summary>
    public string? LastRefreshError { get; set; }
}
