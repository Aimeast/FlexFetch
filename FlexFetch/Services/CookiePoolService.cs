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
    private readonly object _lock = new();

    public CookiePoolService(ICookieRepository repo)
    {
        _repo = repo;
    }

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
                if (!IsDomainMatch(host, domain))
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
                var group = FindGroupFor(cookie.Domain) ?? GetOrCreateGroup(DefaultGroupName);
                var existing = group.Cookies.FirstOrDefault(c =>
                    string.Equals(c.Domain, cookie.Domain, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.Path ?? "/", cookie.Path ?? "/", StringComparison.Ordinal)
                    && string.Equals(c.Name, cookie.Name, StringComparison.Ordinal));
                if (existing is null)
                {
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

    /// <summary>Imports pasted text, auto-detecting the format (Netscape file or Set-Cookie header).</summary>
    public CookieImportResult ImportText(Uri url, string text, string? groupName = null)
    {
        if (text.Contains('\t') && (text.Contains("# HttpOnly") || text.Contains("#HttpOnly_") || LooksLikeNetscape(text)))
        {
            return ImportNetscape(text, groupName);
        }

        // Fall back to Set-Cookie style ("name=value; Path=...; Domain=...").
        return ImportSetCookie(url, new[] { text }, groupName);
    }

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

    public bool DeleteGroup(string id) => _repo.DeleteGroup(id);

    // --- Internals ---

    private bool AddToGroup(string? groupName, CookieItem cookie, List<string> errors)
    {
        lock (_lock)
        {
            var group = string.IsNullOrWhiteSpace(groupName)
                ? FindGroupFor(cookie.Domain) ?? GetOrCreateGroup(DefaultGroupName)
                : GetOrCreateGroup(groupName!);

            if (group.Cookies.Any(c =>
                    string.Equals(c.Domain, cookie.Domain, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.Path ?? "/", cookie.Path ?? "/", StringComparison.Ordinal)
                    && string.Equals(c.Name, cookie.Name, StringComparison.Ordinal)))
            {
                var existing = group.Cookies.First(c =>
                    string.Equals(c.Domain, cookie.Domain, StringComparison.OrdinalIgnoreCase)
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
        foreach (var group in _repo.GetAllGroups())
        {
            if (group.Cookies.Any(c => string.Equals(c.Domain, domain, StringComparison.OrdinalIgnoreCase)))
            {
                return group;
            }
        }
        return null;
    }

    private CookieGroup GetOrCreateGroup(string name)
    {
        var existing = _repo.GetGroupByName(name);
        if (existing is not null)
        {
            return existing;
        }

        var group = new CookieGroup { Name = name };
        _repo.InsertGroup(group);
        return group;
    }

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
