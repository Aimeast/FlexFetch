using FlexFetch.Entities;

namespace FlexFetch.Services.Session;

/// <summary>Health verdicts of the X (Twitter) session.</summary>
public enum TwitterSessionHealth
{
    Unknown,

    /// <summary>x.com accepted the auth_token (fresh ct0/twid came back).</summary>
    Ok,

    /// <summary>The token no longer carries a session (revoked/logged out);
    /// only a fresh import fixes it.</summary>
    Invalid,

    /// <summary>The check errored (network, timeout) - the token may still
    /// be fine.</summary>
    Error,
}

/// <summary>Metadata of the X (Twitter) session file.</summary>
public sealed class TwitterSessionMeta
{
    /// <summary>Best-effort screen name of the account the token belongs to
    /// (scraped from the logged-in page shell at import/check time).</summary>
    public string? Account { get; set; }

    /// <summary>Numeric account id from the twid cookie.</summary>
    public string? UserId { get; set; }

    public DateTime? ImportedAt { get; set; }

    public DateTime? LastCheckAt { get; set; }

    public string Health { get; set; } = TwitterSessionHealth.Unknown.ToString();

    public string? LastError { get; set; }
}

/// <summary>
/// Store of the X (Twitter) session file - the auth_token cookie jar that
/// unlocks sensitive-media posts for the Twitter downloader, held as one
/// encrypted envelope (session/twitter.session). Nothing consumes the file
/// directly: the Twitter downloader reads the jar in memory and derives the
/// per-request csrf token (ct0) itself.
/// </summary>
public sealed class TwitterSessionStore
{
    public const string SiteName = "twitter";

    private readonly SessionFileStore _files;

    public TwitterSessionStore(SessionFileStore files)
    {
        _files = files;
    }

    /// <summary>True when a session file exists.</summary>
    public bool Exists => _files.Exists(SiteName);

    /// <summary>Loads the session cookies (empty when missing/unreadable).</summary>
    public IReadOnlyList<CookieItem> ReadCookies()
    {
        try
        {
            return _files.Read<TwitterSessionMeta>(SiteName)?.Cookies ?? new List<CookieItem>();
        }
        catch (SessionFileUnreadableException)
        {
            return Array.Empty<CookieItem>();
        }
    }

    /// <summary>Loads the session metadata, or null when missing/unreadable.</summary>
    public TwitterSessionMeta? ReadMeta()
    {
        try
        {
            return _files.Read<TwitterSessionMeta>(SiteName)?.Meta;
        }
        catch (SessionFileUnreadableException)
        {
            return null;
        }
    }

    /// <summary>Atomically replaces the session file (cookies + metadata).</summary>
    public void Write(IReadOnlyList<CookieItem> cookies, TwitterSessionMeta meta)
    {
        _files.Write(SiteName, new SessionFile<TwitterSessionMeta> { Cookies = cookies.ToList(), Meta = meta });
    }

    /// <summary>Updates only the metadata (e.g. check verdicts). Skipped
    /// when no session file exists - a meta-only update must not resurrect
    /// a cleared session (the downloader records verdicts from concurrent
    /// download tasks, which can race a clearance).</summary>
    public void UpdateMeta(Action<TwitterSessionMeta> update)
    {
        try
        {
            _files.Update<TwitterSessionMeta>(SiteName, file =>
            {
                file.Meta ??= new TwitterSessionMeta();
                update(file.Meta);
            });
        }
        catch (SessionFileUnreadableException)
        {
            // A meta-only update cannot repair an unreadable file; the next
            // successful import replaces it wholesale.
        }
    }

    /// <summary>Removes the session file (anonymous mode again).</summary>
    public void Delete() => _files.Delete(SiteName);

    /// <summary>
    /// Keeps only cookies whose domain belongs to x.com or twitter.com (the
    /// session's minimal form). twimg.com is not imported: the media CDN is
    /// anonymous anyway.
    /// </summary>
    public static IReadOnlyList<CookieItem> FilterTwitterDomain(IReadOnlyList<CookieItem> cookies) =>
        cookies.Where(c => IsTwitterDomain(c.Domain)).ToList();

    /// <summary>True when the (leading-dot-insensitive) domain is x.com or twitter.com or a subdomain.</summary>
    public static bool IsTwitterDomain(string? domain)
    {
        var d = (domain ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return d.EndsWith(".x.com", StringComparison.OrdinalIgnoreCase)
            || d.Equals("x.com", StringComparison.OrdinalIgnoreCase)
            || d.EndsWith(".twitter.com", StringComparison.OrdinalIgnoreCase)
            || d.Equals("twitter.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Clones base-domain cookies to the sibling domain: a session imported
    /// from x.com must also serve twitter.com links (and the other way
    /// round) - both hostnames are live and the stored jar is the only
    /// cookie source. Subdomain-scoped cookies are left untouched.
    /// </summary>
    public static IReadOnlyList<CookieItem> MirrorSiblingDomain(IReadOnlyList<CookieItem> cookies)
    {
        var mirrored = new List<CookieItem>(cookies);
        foreach (var cookie in cookies)
        {
            var sibling = SiblingDomain(cookie.Domain);
            if (sibling is null)
            {
                continue;
            }

            mirrored.Add(new CookieItem
            {
                Domain = sibling,
                Path = cookie.Path,
                Name = cookie.Name,
                Value = cookie.Value,
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                SameSite = cookie.SameSite,
                ExpiresAt = cookie.ExpiresAt,
            });
        }

        // Merge dedupes by (name, domain) and drops expired entries, so an
        // import that already carried both domains stays as-is.
        return Merge(mirrored, Array.Empty<CookieItem>());
    }

    private static string? SiblingDomain(string? domain)
    {
        var d = (domain ?? string.Empty).TrimStart('.').ToLowerInvariant();
        if (d.Equals("x.com", StringComparison.OrdinalIgnoreCase))
        {
            return ".twitter.com";
        }

        if (d.Equals("twitter.com", StringComparison.OrdinalIgnoreCase))
        {
            return ".x.com";
        }

        return null;
    }

    /// <summary>
    /// Merges the incoming cookies over the existing ones by (name, domain)
    /// key; expired cookies are dropped. Same semantics as the YouTube jar.
    /// </summary>
    public static IReadOnlyList<CookieItem> Merge(IReadOnlyList<CookieItem> existing, IReadOnlyList<CookieItem> incoming)
    {
        var map = new Dictionary<(string Name, string Domain), CookieItem>();
        foreach (var c in existing)
        {
            if (!IsExpired(c))
            {
                map[Key(c)] = c;
            }
        }

        foreach (var c in incoming)
        {
            if (IsExpired(c))
            {
                map.Remove(Key(c));
                continue;
            }

            map[Key(c)] = c;
        }

        return map.Values.ToList();
    }

    private static (string, string) Key(CookieItem c) =>
        (c.Name, (c.Domain ?? string.Empty).TrimStart('.').ToLowerInvariant());

    private static bool IsExpired(CookieItem c) =>
        c.ExpiresAt is not null && c.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow;

    /// <summary>
    /// Extracts a cookie value from raw Set-Cookie header lines (the handlers
    /// run with UseCookies=false, so responses carry them verbatim). Returns
    /// the first match's value without attributes.
    /// </summary>
    public static string? ExtractSetCookieValue(IReadOnlyList<string> setCookies, string name) =>
        setCookies.Where(v => v.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            .Select(v => v[(name.Length + 1)..])
            .Select(v => v.Contains(';') ? v[..v.IndexOf(';')] : v)
            .FirstOrDefault();
}
