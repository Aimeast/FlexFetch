using FlexFetch.Entities;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Netscape-format cookie file helpers: serialize a snapshot/candidate jar
/// to text and parse such text back. The text lands wherever the caller
/// needs it (the snapshot file, one-time copies handed to yt-dlp, candidate
/// jars for probes) - this class owns only the format.
/// </summary>
public static class CookieFile
{
    public const string HttpOnlyPrefix = "#HttpOnly_";

    /// <summary>Serializes cookies into Netscape HTTP Cookie File text (yt-dlp compatible).</summary>
    public static string BuildContent(IReadOnlyList<CookieItem> cookies)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Netscape HTTP Cookie File");
        foreach (var c in cookies)
        {
            var secure = c.Secure ? "TRUE" : "FALSE";
            var expiry = c.ExpiresAt is { } exp
                ? new DateTimeOffset(exp.ToUniversalTime()).ToUnixTimeSeconds().ToString()
                : "0";
            var path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path;
            // includeSubdomains: TRUE only for domain cookies (leading dot).
            // A host-only domain must round-trip unchanged, never widened to
            // its subdomains.
            var includeSubdomains = c.Domain.StartsWith('.') ? "TRUE" : "FALSE";
            var domain = c.HttpOnly ? HttpOnlyPrefix + c.Domain : c.Domain;

            sb.Append(domain).Append('\t').Append(includeSubdomains).Append('\t')
                .Append(path).Append('\t')
                .Append(secure).Append('\t')
                .Append(expiry).Append('\t')
                .Append(c.Name).Append('\t')
                .Append(c.Value).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses a Netscape cookie file back into cookie items. The HttpOnly
    /// prefix must be checked BEFORE the generic comment check: the core
    /// session family is HttpOnly, and a plain StartsWith('#') test silently
    /// drops every one of those lines. Returns an empty list when the file is
    /// missing or unreadable.
    /// </summary>
    public static IReadOnlyList<CookieItem> Read(string path)
    {
        var result = new List<CookieItem>();
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var httpOnly = line.StartsWith(HttpOnlyPrefix, StringComparison.Ordinal);
            if (httpOnly)
            {
                line = line[HttpOnlyPrefix.Length..];
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 7)
            {
                continue;
            }

            var expires = long.TryParse(parts[4], out var secs) && secs > 0
                ? (DateTime?)DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime
                : null;
            result.Add(new CookieItem
            {
                Domain = parts[0],
                Path = parts[2],
                Secure = string.Equals(parts[3], "TRUE", StringComparison.OrdinalIgnoreCase),
                HttpOnly = httpOnly,
                ExpiresAt = expires,
                Name = parts[5],
                Value = parts[6],
            });
        }

        return result;
    }
}
