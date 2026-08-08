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
        ".ts", ".m3u8", ".mpd",
        ".mp3", ".m4a", ".aac", ".ogg", ".wav", ".flac",
        ".zip", ".tar", ".gz", ".7z", ".pdf", ".exe", ".bin",
    };

    /// <summary>True when the URL path ends with a known media/download extension.</summary>
    public static bool HasMediaExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return MediaExtensions.Contains(Path.GetExtension(uri.AbsolutePath));
    }

    /// <summary>
    /// Probes the URL with a HEAD request and returns the response
    /// Content-Type, or null when the probe fails or the URL is a web page.
    /// </summary>
    public static async Task<string?> ProbeContentTypeAsync(
        string url,
        IProxyService proxy,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var handler = proxy.CreateHandler(uri);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return response.Content.Headers.ContentType?.MediaType;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
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
}
