using FlexFetch.Entities;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Netscape-format cookie file helpers used to hand cookies to yt-dlp
/// (--cookies): export pool cookies to a per-task file, parse the file back
/// after the process finished (yt-dlp may have refreshed cookies), and clean
/// up the temporary file.
/// </summary>
public static class CookieFile
{
    /// <summary>Per-task cookie file path under the data directory.</summary>
    public static string GetPath(string dataDir, string taskId) =>
        Path.Combine(dataDir, "cookies", $"{taskId}.txt");

    /// <summary>Exports pool cookies to a Netscape-format file for the task.</summary>
    public static void Write(string dataDir, string taskId, IReadOnlyList<CookieItem> cookies)
    {
        var dir = Path.Combine(dataDir, "cookies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(GetPath(dataDir, taskId), BuildContent(cookies));
    }

    /// <summary>Serializes cookies into Netscape HTTP Cookie File text (yt-dlp compatible).</summary>
    public static string BuildContent(IReadOnlyList<CookieItem> cookies)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Netscape HTTP Cookie File");
        foreach (var c in cookies)
        {
            var domain = c.HttpOnly ? "#HttpOnly_" + c.Domain : c.Domain;
            var secure = c.Secure ? "TRUE" : "FALSE";
            var expiry = c.ExpiresAt is { } exp
                ? new DateTimeOffset(exp.ToUniversalTime()).ToUnixTimeSeconds().ToString()
                : "0";
            sb.Append(domain).Append('\t').Append("TRUE").Append('\t')
                .Append(string.IsNullOrEmpty(c.Path) ? "/" : c.Path).Append('\t')
                .Append(secure).Append('\t')
                .Append(expiry).Append('\t')
                .Append(c.Name).Append('\t')
                .Append(c.Value).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses a Netscape cookie file back into pool items (used to write back
    /// whatever yt-dlp refreshed). Returns an empty list when the file is
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
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length < 7)
            {
                continue;
            }

            var domain = parts[0];
            var httpOnly = domain.StartsWith("#HttpOnly_", StringComparison.Ordinal);
            if (httpOnly)
            {
                domain = domain["#HttpOnly_".Length..];
            }

            var expires = long.TryParse(parts[4], out var secs) && secs > 0
                ? (DateTime?)DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime
                : null;
            result.Add(new CookieItem
            {
                Domain = domain,
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

    /// <summary>Removes the task's temporary cookie file, if present.</summary>
    public static void Delete(string dataDir, string taskId)
    {
        var path = GetPath(dataDir, taskId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
