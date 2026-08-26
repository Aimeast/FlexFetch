using System.Text.Json;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Enums;

namespace FlexFetch.Services;

/// <summary>Result of importing cookies from an external source.</summary>
public sealed record CookieImportResult(int Imported, int Skipped, IReadOnlyList<string> Errors);

/// <summary>
/// Centralized cookie pool: the single source of truth for cookies shared by
/// all download channels (HttpClient, yt-dlp, browser). Enforces RFC 6265
/// matching rules - domain match, no cross-site sharing, no expired sessions -
/// and supports Netscape files, pasted text and Set-Cookie headers import.
/// </summary>
public sealed class CookiePoolService
{
    private const string DefaultGroupName = "default";

    private readonly ICookieRepository _repo;
    private readonly IReadOnlyList<ICookieDomainMapping> _mappings;
    private readonly ICookieDomainMapping _default = new DefaultCookieDomainMapping();
    private readonly object _lock = new();
    private readonly object _refreshLock = new();
    private bool _refreshing;

    public CookiePoolService(ICookieRepository repo, IEnumerable<ICookieDomainMapping> mappings)
    {
        _repo = repo;
        _mappings = mappings.ToList();
    }

    /// <summary>
    /// Selects the site-specific domain mapping for a group, falling back to
    /// the default (no sharing) when none matches.
    /// </summary>
    private ICookieDomainMapping SelectMapping(CookieGroup group) =>
        _mappings.FirstOrDefault(m => m.IsMatch(group)) ?? _default;

    // --- Read / match ---

    /// <summary>Returns cookies matching the URL per RFC 6265 (domain/path/secure/expiry).</summary>
    public IReadOnlyList<CookieItem> GetCookiesForUrl(Uri url)
    {
        var isHttps = string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var host = url.Host.TrimStart('.');
        var path = NormalizePath(url.AbsolutePath);

        var result = new List<CookieItem>();
        foreach (var group in _repo.GetAllGroups())
        {
            foreach (var cookie in group.Cookies)
            {
                if (IsExpired(cookie))
                {
                    continue;
                }

                var domain = (cookie.Domain ?? string.Empty).TrimStart('.');
                var sharedMatch = (cookie.SharedDomains ?? new())
                    .Any(d => IsDomainMatch(host, d.TrimStart('.')));
                if (!IsDomainMatch(host, domain) && !sharedMatch)
                {
                    continue;
                }
                if (cookie.Secure && !isHttps)
                {
                    continue;
                }
                if (!IsPathMatch(path, string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path))
                {
                    continue;
                }
                if (cookie.Name.Contains(';') || cookie.Value.Contains(';'))
                {
                    continue; // would truncate the Cookie header
                }

                result.Add(cookie);
            }
        }

        return result;
    }

    /// <summary>Builds a Cookie header value for the URL (RFC 6265 matching).</summary>
    public string GetCookieHeader(Uri url)
    {
        var cookies = GetCookiesForUrl(url);
        return string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
    }

    /// <summary>Merges cookies captured by a download channel back into the pool (write-back).</summary>
    public void UpsertCookies(IEnumerable<CookieItem> cookies)
    {
        lock (_lock)
        {
            foreach (var cookie in cookies.Where(c => !string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(c.Domain)))
            {
                // A sibling-domain entry (e.g. .google.com SID) is the same shared
                // cookie as the stored primary entry (e.g. .youtube.com SID with
                // SharedDomains=[".google.com"]): update the single stored entry.
                var ownerEntry = FindSharedOwnerEntry(cookie);
                if (ownerEntry is not null)
                {
                    var owner = ownerEntry.Value.Cookie;
                    owner.Value = cookie.Value;
                    owner.ExpiresAt = cookie.ExpiresAt;
                    owner.Secure = cookie.Secure;
                    owner.HttpOnly = cookie.HttpOnly;
                    owner.SameSite = cookie.SameSite;
                    var ownerGroup = ownerEntry.Value.Group;
                    ownerGroup.UpdatedAt = DateTime.UtcNow;
                    _repo.UpdateGroup(ownerGroup);
                    continue;
                }

                var group = FindGroupFor(cookie.Domain) ?? GetOrCreateGroup(DefaultGroupName);
                var existing = group.Cookies.FirstOrDefault(c =>
                    string.Equals(c.Domain, cookie.Domain, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.Path ?? "/", cookie.Path ?? "/", StringComparison.Ordinal)
                    && string.Equals(c.Name, cookie.Name, StringComparison.Ordinal));
                if (existing is null)
                {
                    var shared = SelectMapping(group).GetSharedDomains(cookie.Domain, cookie.Name);
                    if (shared.Count > 0)
                    {
                        cookie.SharedDomains = shared.ToList();
                    }

                    group.Cookies.Add(cookie);
                }
                else
                {
                    existing.Value = cookie.Value;
                    existing.ExpiresAt = cookie.ExpiresAt;
                    existing.Secure = cookie.Secure;
                    existing.HttpOnly = cookie.HttpOnly;
                    existing.SameSite = cookie.SameSite;
                }

                group.UpdatedAt = DateTime.UtcNow;
                _repo.UpdateGroup(group);
            }
        }
    }

    // --- Import ---

    /// <summary>Imports a Netscape-format cookie file content (browser extension export).</summary>
    public CookieImportResult ImportNetscape(string content, string? groupName = null)
    {
        var errors = new List<string>();
        var imported = 0;
        var skipped = 0;

        var cookies = ParseNetscape(content, errors);
        foreach (var cookie in cookies)
        {
            var ok = AddToGroup(groupName, cookie, errors);
            if (ok) imported++; else skipped++;
        }

        return new CookieImportResult(imported, skipped, errors);
    }

    /// <summary>Imports cookies from Set-Cookie response headers received for the URL.</summary>
    public CookieImportResult ImportSetCookie(Uri url, IEnumerable<string> headers, string? groupName = null)
    {
        var errors = new List<string>();
        var imported = 0;
        var skipped = 0;

        foreach (var header in headers.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            foreach (var cookie in ParseSetCookie(url, header, errors))
            {
                var ok = AddToGroup(groupName, cookie, errors);
                if (ok) imported++; else skipped++;
            }
        }

        return new CookieImportResult(imported, skipped, errors);
    }

    /// <summary>Imports pasted text, auto-detecting the format (JSON, Netscape file or Set-Cookie header).</summary>
    public CookieImportResult ImportText(Uri? url, string text, string? groupName = null)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return ImportJson(text, groupName);
        }

        if (text.Contains('\t') && (text.Contains("# HttpOnly") || text.Contains("#HttpOnly_") || LooksLikeNetscape(text)))
        {
            return ImportNetscape(text, groupName);
        }

        // Fall back to Set-Cookie style ("name=value; Path=...; Domain=...").
        if (url is null)
        {
            return new CookieImportResult(0, 0, new List<string> { "Site URL is required for Set-Cookie text" });
        }

        return ImportSetCookie(url, new[] { text }, groupName);
    }

    /// <summary>Imports cookies from a browser-extension JSON export (array or { cookies: [...] }).</summary>
    public CookieImportResult ImportJson(string content, string? groupName = null)
    {
        var errors = new List<string>();
        var imported = 0;
        var skipped = 0;

        foreach (var cookie in ParseJson(content, errors))
        {
            var ok = AddToGroup(groupName, cookie, errors);
            if (ok) imported++; else skipped++;
        }

        return new CookieImportResult(imported, skipped, errors);
    }

    /// <summary>Imports an already-edited cookie list into the pool.</summary>
    public CookieImportResult ImportItems(IEnumerable<CookieItem> cookies, string? groupName = null)
    {
        var errors = new List<string>();
        var imported = 0;
        var skipped = 0;

        foreach (var cookie in cookies)
        {
            var ok = AddToGroup(groupName, cookie, errors);
            if (ok) imported++; else skipped++;
        }

        return new CookieImportResult(imported, skipped, errors);
    }

    private static List<CookieItem> ParseJson(string content, List<string> errors)
    {
        var result = new List<CookieItem>();
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // Accept either a bare array or { "cookies": [...] }.
            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.TryGetProperty("cookies", out var wrapped) && wrapped.ValueKind == JsonValueKind.Array)
            {
                array = wrapped;
            }
            else
            {
                errors.Add("JSON must be a cookie array or { cookies: [...] }");
                return result;
            }

            foreach (var item in array.EnumerateArray())
            {
                var domain = GetString(item, "domain");
                var name = GetString(item, "name");
                if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(name))
                {
                    errors.Add("Cookie entry missing domain or name");
                    continue;
                }

                var expires = item.TryGetProperty("expirationDate", out var exp) && exp.ValueKind == JsonValueKind.Number
                    ? exp.GetDouble()
                    : 0;
                var session = item.TryGetProperty("session", out var sess) && sess.ValueKind == JsonValueKind.True;
                result.Add(new CookieItem
                {
                    Domain = domain,
                    Path = string.IsNullOrEmpty(GetString(item, "path")) ? "/" : GetString(item, "path")!,
                    Secure = GetBool(item, "secure"),
                    HttpOnly = GetBool(item, "httpOnly"),
                    ExpiresAt = !session && expires > 0
                        ? DateTimeOffset.FromUnixTimeSeconds((long)expires).UtcDateTime
                        : null,
                    Name = name,
                    Value = GetString(item, "value") ?? string.Empty,
                });
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"Invalid JSON: {ex.Message}");
        }

        return result;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // --- Groups ---

    public IReadOnlyList<CookieGroup> GetGroups() => _repo.GetAllGroups();

    public CookieGroup CreateGroup(string name, IEnumerable<string> urls)
    {
        var group = new CookieGroup
        {
            Name = name,
            Urls = urls.ToList(),
        };
        _repo.InsertGroup(group);
        return group;
    }

    public bool UpdateGroup(CookieGroup group) => _repo.UpdateGroup(group);

    /// <summary>Replaces the refresh URLs of a group (normalized, deduplicated).</summary>
    public bool UpdateGroupUrls(string id, IEnumerable<string> urls)
    {
        lock (_lock)
        {
            var group = _repo.GetGroupById(id);
            if (group is null)
            {
                return false;
            }

            group.Urls = urls
                .Select(u => u.Trim())
                .Where(u => u.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            group.UpdatedAt = DateTime.UtcNow;
            return _repo.UpdateGroup(group);
        }
    }

    public bool DeleteGroup(string id) => _repo.DeleteGroup(id);

    /// <summary>True while a refresh run (manual or scheduled) is in progress.</summary>
    public bool IsRefreshing => _refreshing;

    /// <summary>Attempts to start a refresh run; false when one is already running.</summary>
    public bool TryStartRefresh()
    {
        lock (_refreshLock)
        {
            if (_refreshing)
            {
                return false;
            }

            _refreshing = true;
            return true;
        }
    }

    /// <summary>Ends a refresh run, allowing the next one to start.</summary>
    public void EndRefresh()
    {
        lock (_refreshLock)
        {
            _refreshing = false;
        }
    }

    /// <summary>Marks a group as currently being refreshed.</summary>
    public bool MarkGroupRefreshing(string id)
    {
        lock (_lock)
        {
            var group = _repo.GetGroupById(id);
            if (group is null)
            {
                return false;
            }

            group.LastRefreshStatus = CookieRefreshStatus.Running;
            group.LastRefreshError = null;
            return _repo.UpdateGroup(group);
        }
    }

    /// <summary>Marks a group refresh as completed successfully.</summary>
    public bool MarkGroupRefreshed(string id)
    {
        lock (_lock)
        {
            var group = _repo.GetGroupById(id);
            if (group is null)
            {
                return false;
            }

            group.LastRefreshStatus = CookieRefreshStatus.Ok;
            group.LastRefreshedAt = DateTime.UtcNow;
            group.LastRefreshError = null;
            return _repo.UpdateGroup(group);
        }
    }

    /// <summary>Marks a group refresh as failed.</summary>
    public bool MarkGroupRefreshFailed(string id, string error)
    {
        lock (_lock)
        {
            var group = _repo.GetGroupById(id);
            if (group is null)
            {
                return false;
            }

            group.LastRefreshStatus = CookieRefreshStatus.Failed;
            group.LastRefreshError = error;
            return _repo.UpdateGroup(group);
        }
    }

    /// <summary>Removes a single cookie from a group (matched by normalized domain, path and name).</summary>
    public bool RemoveCookie(string? groupName, string domain, string path, string name)
    {
        lock (_lock)
        {
            var group = string.IsNullOrWhiteSpace(groupName)
                ? FindGroupFor(domain)
                : _repo.GetGroupByName(groupName!);
            if (group is null)
            {
                return false;
            }

            var removed = group.Cookies.RemoveAll(c =>
                string.Equals(NormalizeDomain(c.Domain), NormalizeDomain(domain), StringComparison.Ordinal)
                && string.Equals(c.Path ?? "/", path ?? "/", StringComparison.Ordinal)
                && string.Equals(c.Name, name, StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            group.UpdatedAt = DateTime.UtcNow;
            _repo.UpdateGroup(group);
            return true;
        }
    }

    /// <summary>
    /// Deletes cookies that existed in the pool when the refresh started
    /// (the before snapshot) but are no longer exported by the browser
    /// (removed by the site, or expired). Cookies written by concurrent
    /// tasks after the snapshot was taken are not in "before", so they are
    /// kept. Returns the number of cookies removed.
    /// </summary>
    public int SyncGroupCookies(string groupId, IReadOnlyList<CookieItem> before, IReadOnlyList<CookieItem> exported)
    {
        lock (_lock)
        {
            var group = _repo.GetGroupById(groupId);
            if (group is null)
            {
                return 0;
            }

            var removed = group.Cookies.RemoveAll(c =>
                before.Any(b => Matches(b, c))
                && !exported.Any(e => Matches(e, c))
                && !exported.Any(e => IsSharedIdentity(e, c)));
            if (removed > 0)
            {
                group.UpdatedAt = DateTime.UtcNow;
                _repo.UpdateGroup(group);
            }

            return removed;
        }
    }

    private static bool Matches(CookieItem a, CookieItem b) =>
        string.Equals(NormalizeDomain(a.Domain), NormalizeDomain(b.Domain), StringComparison.Ordinal)
        && string.Equals(a.Path ?? "/", b.Path ?? "/", StringComparison.Ordinal)
        && string.Equals(a.Name, b.Name, StringComparison.Ordinal);

    /// <summary>
    /// Finds the stored primary entry of a sibling-domain cookie (e.g. the
    /// .youtube.com SID for an incoming .google.com SID) together with its
    /// group, or null when the cookie is not a shared sibling of any stored
    /// cookie. The group and cookie come from the same object graph so the
    /// caller can modify the cookie and persist the group in one update.
    /// </summary>
    private (CookieGroup Group, CookieItem Cookie)? FindSharedOwnerEntry(CookieItem cookie)
    {
        var incomingDomain = NormalizeDomain(cookie.Domain);
        foreach (var group in _repo.GetAllGroups())
        {
            foreach (var c in group.Cookies)
            {
                if (!string.Equals(NormalizeDomain(c.Name), NormalizeDomain(cookie.Name), StringComparison.Ordinal))
                {
                    continue;
                }

                if ((c.SharedDomains ?? new()).Select(NormalizeDomain)
                    .Contains(incomingDomain, StringComparer.Ordinal))
                {
                    return (group, c);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when two cookies are the same shared identity (same name, and one
    /// domain is the other's shared sibling), so a sibling-domain entry keeps
    /// the primary entry alive during sync-delete.
    /// </summary>
    private static bool IsSharedIdentity(CookieItem a, CookieItem b)
    {
        if (!string.Equals(NormalizeDomain(a.Name), NormalizeDomain(b.Name), StringComparison.Ordinal))
        {
            return false;
        }

        var aDomain = NormalizeDomain(a.Domain);
        var bDomain = NormalizeDomain(b.Domain);
        var aShared = (a.SharedDomains ?? new()).Select(NormalizeDomain);
        var bShared = (b.SharedDomains ?? new()).Select(NormalizeDomain);
        return aShared.Contains(bDomain, StringComparer.Ordinal)
            || bShared.Contains(aDomain, StringComparer.Ordinal);
    }

    // --- Internals ---

    private bool AddToGroup(string? groupName, CookieItem cookie, List<string> errors)
    {
        lock (_lock)
        {
            var group = string.IsNullOrWhiteSpace(groupName)
                ? FindGroupFor(cookie.Domain) ?? GetOrCreateGroup(NormalizeDomain(cookie.Domain))
                : GetOrCreateGroup(groupName!);

            // Mark cross-domain shared cookies (e.g. Google identity cookies)
            // with their sibling domains so reads can materialize them there.
            var shared = SelectMapping(group).GetSharedDomains(cookie.Domain, cookie.Name);
            if (shared.Count > 0)
            {
                cookie.SharedDomains = shared.ToList();
            }

            // One domain per group: refuse cookies for a different domain,
            // so the group list can show the domain name instead of a column.
            if (group.Cookies.Count > 0
                && !string.Equals(NormalizeDomain(group.Cookies[0].Domain), NormalizeDomain(cookie.Domain), StringComparison.Ordinal))
            {
                errors.Add($"Group '{group.Name}' is for domain '{group.Cookies[0].Domain}', not '{cookie.Domain}'");
                return false;
            }

            if (group.Cookies.Any(c =>
                    string.Equals(NormalizeDomain(c.Domain), NormalizeDomain(cookie.Domain), StringComparison.Ordinal)
                    && string.Equals(c.Path ?? "/", cookie.Path ?? "/", StringComparison.Ordinal)
                    && string.Equals(c.Name, cookie.Name, StringComparison.Ordinal)))
            {
                var existing = group.Cookies.First(c =>
                    string.Equals(NormalizeDomain(c.Domain), NormalizeDomain(cookie.Domain), StringComparison.Ordinal)
                    && string.Equals(c.Path ?? "/", cookie.Path ?? "/", StringComparison.Ordinal)
                    && string.Equals(c.Name, cookie.Name, StringComparison.Ordinal));
                existing.Value = cookie.Value;
                existing.ExpiresAt = cookie.ExpiresAt;
                existing.Secure = cookie.Secure;
                existing.HttpOnly = cookie.HttpOnly;
                existing.SameSite = cookie.SameSite;
            }
            else
            {
                group.Cookies.Add(cookie);
            }

            group.UpdatedAt = DateTime.UtcNow;
            _repo.UpdateGroup(group);
            return true;
        }
    }

    private CookieGroup? FindGroupFor(string domain)
    {
        var normalized = NormalizeDomain(domain);
        foreach (var group in _repo.GetAllGroups())
        {
            if (group.Cookies.Any(c => string.Equals(NormalizeDomain(c.Domain), normalized, StringComparison.Ordinal)))
            {
                return group;
            }
        }
        return null;
    }

    private CookieGroup GetOrCreateGroup(string name)
    {
        var normalized = NormalizeDomain(name);
        var existing = _repo.GetGroupByName(name)
            ?? _repo.GetAllGroups().FirstOrDefault(g =>
                string.Equals(NormalizeDomain(g.Name), normalized, StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing;
        }

        var group = new CookieGroup { Name = name };
        _repo.InsertGroup(group);
        return group;
    }

    /// <summary>Canonical form of a cookie domain: no leading dot, lowercase.</summary>
    private static string NormalizeDomain(string? domain) =>
        (domain ?? string.Empty).TrimStart('.').ToLowerInvariant();

    private static bool IsExpired(CookieItem cookie) =>
        cookie.ExpiresAt is not null && cookie.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow;

    private static bool IsDomainMatch(string host, string cookieDomain)
    {
        if (string.IsNullOrEmpty(cookieDomain))
        {
            return false;
        }

        return string.Equals(host, cookieDomain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + cookieDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathMatch(string requestPath, string cookiePath)
    {
        if (string.Equals(requestPath, cookiePath, StringComparison.Ordinal))
        {
            return true;
        }
        if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal))
        {
            return false;
        }
        return cookiePath.EndsWith('/') || requestPath[cookiePath.Length] == '/';
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/'))
        {
            return "/";
        }
        return path.Length > 1 && path.EndsWith('/') ? path[..^1] : path;
    }

    private static List<CookieItem> ParseNetscape(string content, List<string> errors)
    {
        var cookies = new List<CookieItem>();
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var httpOnly = false;
            if (trimmed.StartsWith("#HttpOnly_", StringComparison.Ordinal))
            {
                httpOnly = true;
                trimmed = trimmed["#HttpOnly_".Length..];
            }
            else if (trimmed.StartsWith('#'))
            {
                continue;
            }

            // domain  includeSubdomains  path  secure  expires  name  value
            var parts = trimmed.Split('\t');
            if (parts.Length < 7)
            {
                errors.Add($"Malformed line: {trimmed[..Math.Min(trimmed.Length, 60)]}");
                continue;
            }

            long.TryParse(parts[4], out var expires);
            cookies.Add(new CookieItem
            {
                Domain = parts[0],
                Path = string.IsNullOrEmpty(parts[2]) ? "/" : parts[2],
                Secure = parts[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                HttpOnly = httpOnly,
                ExpiresAt = expires > 0 ? DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime : null,
                Name = parts[5],
                Value = parts[6],
            });
        }
        return cookies;
    }

    private static IEnumerable<CookieItem> ParseSetCookie(Uri uri, string header, List<string> errors)
    {
        var parts = header.Split(';');
        var nameValue = parts[0].Split('=', 2);
        if (nameValue.Length != 2)
        {
            errors.Add($"Malformed Set-Cookie: {header[..Math.Min(header.Length, 60)]}");
            yield break;
        }

        var name = nameValue[0].Trim();
        var value = nameValue[1].Trim();
        if (name.Length == 0)
        {
            yield break;
        }

        var domain = uri.Host;
        var path = NormalizePath(uri.AbsolutePath);
        var secure = false;
        var httpOnly = false;
        var sameSite = SameSitePolicy.Unspecified;
        DateTime? expiresAt = null;
        long? maxAge = null;

        for (var i = 1; i < parts.Length; i++)
        {
            var pair = parts[i].Split('=', 2);
            var attr = pair[0].Trim();
            var attrValue = pair.Length > 1 ? pair[1].Trim().Trim('"') : string.Empty;

            if (attr.Equals("Domain", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(attrValue))
            {
                domain = attrValue.TrimStart('.');
            }
            else if (attr.Equals("Path", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(attrValue))
            {
                path = attrValue;
            }
            else if (attr.Equals("Secure", StringComparison.OrdinalIgnoreCase))
            {
                secure = true;
            }
            else if (attr.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase))
            {
                httpOnly = true;
            }
            else if (attr.Equals("SameSite", StringComparison.OrdinalIgnoreCase))
            {
                sameSite = attrValue.ToLowerInvariant() switch
                {
                    "strict" => SameSitePolicy.Strict,
                    "lax" => SameSitePolicy.Lax,
                    "none" => SameSitePolicy.None,
                    _ => SameSitePolicy.Unspecified,
                };
            }
            else if (attr.Equals("Expires", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(attrValue, out var exp))
            {
                expiresAt = exp.ToUniversalTime();
            }
            else if (attr.Equals("Max-Age", StringComparison.OrdinalIgnoreCase) && long.TryParse(attrValue, out var age))
            {
                maxAge = age;
            }
        }

        if (maxAge is not null)
        {
            expiresAt = maxAge <= 0 ? DateTime.UtcNow.AddSeconds(-1) : DateTime.UtcNow.AddSeconds(maxAge.Value);
        }

        yield return new CookieItem
        {
            Domain = domain,
            Path = path,
            Name = name,
            Value = value,
            Secure = secure,
            HttpOnly = httpOnly,
            SameSite = sameSite,
            ExpiresAt = expiresAt,
        };
    }

    private static bool LooksLikeNetscape(string text)
    {
        // Netscape lines are tab-separated with 7+ fields.
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }
            return trimmed.Split('\t').Length >= 7;
        }
        return false;
    }
}
