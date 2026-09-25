using FlexFetch.Entities;
using FlexFetch.Services.Session;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Netscape-format cookie file helpers: serialize a snapshot/candidate jar
/// to text and read such files back (the shared text-format parsing lives
/// in CookieTextParser). The text lands wherever the caller needs it (the
/// snapshot file, one-time copies handed to yt-dlp, candidate jars for
/// probes).
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
    /// Parses a Netscape cookie file back into cookie items (the format is
    /// parsed by CookieTextParser). Returns an empty list when the file is
    /// missing or unreadable.
    /// </summary>
    public static IReadOnlyList<CookieItem> Read(string path) =>
        File.Exists(path)
            ? CookieTextParser.ParseNetscape(File.ReadAllText(path)).Cookies
            : [];
}
