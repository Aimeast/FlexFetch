using System.Net;
using System.Net.Http.Headers;
using FlexFetch.Services.Routing;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Detects whether a URL is a direct media file link (as opposed to a web
/// page). Detection is two-tiered: a fast extension check on the URL path,
/// and - when the extension is ambiguous - a HEAD probe of the response
/// Content-Type (some direct videos have no media extension and are only
/// identifiable by their MIME type).
/// </summary>
public static class DirectLinkDetector
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mkv", ".mov", ".avi", ".flv",
        ".ts", ".m3u8", ".m3u", ".mpd",
        ".mp3", ".m4a", ".aac", ".ogg", ".wav", ".flac",
        ".zip", ".tar", ".gz", ".7z", ".pdf", ".exe", ".bin",
    };

    /// <summary>
    /// True when the URL points at a playlist manifest (.m3u/.m3u8 HLS or
    /// media list, .mpd DASH). Manifests must not be saved as raw files by
    /// the generic downloader: yt-dlp parses them into either a playable
    /// stream (HLS) or a playlist expansion (plain m3u lists).
    /// </summary>
    public static bool IsManifestExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var ext = Path.GetExtension(uri.AbsolutePath);
        return ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the URL path ends with a known media/download extension.</summary>
    public static bool HasMediaExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return MediaExtensions.Contains(Path.GetExtension(uri.AbsolutePath));
    }

    /// <summary>What the probe learned about a direct link: the served media
    /// type and, when the server advertises one, the real file name
    /// (Content-Disposition).</summary>
    public sealed record DirectLinkProbe(string? ContentType, string? FileName);

    /// <summary>
    /// Probes the URL for its media type and real file name. HEAD goes first
    /// (the cheap form static file servers answer); a server that rejects or
    /// mishandles HEAD falls back to a one-byte Range GET, which succeeds
    /// wherever the actual download would. Returns null only when the URL is
    /// unreachable - the download itself would then fail the same way.
    /// </summary>
    public static async Task<DirectLinkProbe?> ProbeAsync(
        string url,
        IProxyService proxy,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var (contentType, fileName) = await ProbeWithAsync(uri, proxy, HttpMethod.Head, cancellationToken);
        if (contentType is null)
        {
            (contentType, fileName) = await ProbeWithAsync(uri, proxy, HttpMethod.Get, cancellationToken);
        }

        return contentType is null ? null : new DirectLinkProbe(contentType, fileName);
    }

    private static async Task<(string? ContentType, string? FileName)> ProbeWithAsync(
        Uri uri,
        IProxyService proxy,
        HttpMethod method,
        CancellationToken cancellationToken)
    {
        var handler = proxy.CreateHandler(uri);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        using var request = new HttpRequestMessage(method, uri);
        if (method == HttpMethod.Get)
        {
            // A one-byte range: enough for the headers, harmless on servers
            // that ignore Range (they return 200 and the body is never read).
            request.Headers.Range = new RangeHeaderValue(0, 0);
        }

        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (null, null);
            }

            var disposition = response.Content.Headers.ContentDisposition;
            var name = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
            return (
                response.Content.Headers.ContentType?.MediaType,
                string.IsNullOrWhiteSpace(name) ? null : name);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (null, null);
        }
    }

    /// <summary>True when the MIME type identifies a media/download payload.</summary>
    public static bool IsMediaContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var ct = contentType.ToLowerInvariant();
        return ct.StartsWith("video/", StringComparison.Ordinal)
            || ct.StartsWith("audio/", StringComparison.Ordinal)
            || ct.Contains("mpegurl")   // HLS manifest
            || ct.Contains("dash")      // DASH manifest (application/dash+xml)
            || ct.Contains("octet-stream");
    }

    /// <summary>
    /// True when the MIME type identifies a playlist manifest (HLS m3u/m3u8 or
    /// DASH mpd) rather than an actual media payload. Manifests are parsed by
    /// yt-dlp, never saved as raw files by the generic downloader.
    /// </summary>
    public static bool IsManifestContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var ct = contentType.ToLowerInvariant();
        return ct.Contains("mpegurl")
            || ct.Contains("dash");
    }
}
