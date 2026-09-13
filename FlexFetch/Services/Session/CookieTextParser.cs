using System.Text.Json;
using FlexFetch.Entities;
using FlexFetch.Enums;

namespace FlexFetch.Services.Session;

/// <summary>Result of parsing an imported cookie text.</summary>
public sealed record CookieParseResult(IReadOnlyList<CookieItem> Cookies, IReadOnlyList<string> Errors);

/// <summary>
/// Parses cookies from pasted text, auto-detecting the format: a
/// browser-extension JSON export, a Netscape cookie file, or a Set-Cookie
/// header. Set-Cookie parsing needs the site URL for the default domain.
/// </summary>
public static class CookieTextParser
{
    public static CookieParseResult Parse(string text, Uri? url = null)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return ParseJson(text);
        }

        if (text.Contains('\t') && (text.Contains("# HttpOnly") || text.Contains("#HttpOnly_") || LooksLikeNetscape(text)))
        {
            return ParseNetscape(text);
        }

        // Fall back to Set-Cookie style ("name=value; Path=...; Domain=...").
        if (url is null)
        {
            return new CookieParseResult([], ["Site URL is required for Set-Cookie text"]);
        }

        return ParseSetCookie(url, text);
    }

    public static CookieParseResult ParseNetscape(string content)
    {
        var errors = new List<string>();
        var cookies = new List<CookieItem>();
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // The HttpOnly prefix must be checked BEFORE the generic comment
            // check: the core session family is HttpOnly, and a plain
            // StartsWith('#') test silently drops every one of those lines.
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

        return new CookieParseResult(cookies, errors);
    }

    public static CookieParseResult ParseJson(string content)
    {
        var errors = new List<string>();
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
                return new CookieParseResult([], ["JSON must be a cookie array or { cookies: [...] }"]);
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

        return new CookieParseResult(result, errors);
    }

    public static CookieParseResult ParseSetCookie(Uri uri, string header)
    {
        var errors = new List<string>();
        var cookies = new List<CookieItem>();
        var parts = header.Split(';');
        var nameValue = parts[0].Split('=', 2);
        if (nameValue.Length != 2)
        {
            return new CookieParseResult([], [$"Malformed Set-Cookie: {header[..Math.Min(header.Length, 60)]}"]);
        }

        var name = nameValue[0].Trim();
        var value = nameValue[1].Trim();
        if (name.Length == 0)
        {
            return new CookieParseResult([], ["Malformed Set-Cookie: empty name"]);
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

        cookies.Add(new CookieItem
        {
            Domain = domain,
            Path = path,
            Name = name,
            Value = value,
            Secure = secure,
            HttpOnly = httpOnly,
            SameSite = sameSite,
            ExpiresAt = expiresAt,
        });
        return new CookieParseResult(cookies, errors);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

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

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/'))
        {
            return "/";
        }
        return path.Length > 1 && path.EndsWith('/') ? path[..^1] : path;
    }
}
