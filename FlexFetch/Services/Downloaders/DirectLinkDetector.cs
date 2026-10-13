using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using FlexFetch.Services.Routing;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Detects whether a URL is a direct file link (as opposed to a web page)
/// by asking the server: a HEAD probe - falling back to a one-byte Range
/// GET - returns the served Content-Type and, when advertised, the real
/// Content-Disposition file name. The answer routes the URL: a named file
/// or a table-mapped MIME type downloads directly, pages and unknown
/// payloads go to the media extractors.
/// </summary>
public static class DirectLinkDetector
{
    // Playlist manifests are parsed by yt-dlp, never saved as raw files:
    // an HLS manifest becomes a playable stream, a plain m3u list a
    // playlist expansion, a DASH manifest a media set.
    private static readonly HashSet<string> ManifestExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u", ".m3u8", ".mpd",
    };

    /// <summary>True when a file name (URL path or advertised name) is a
    /// playlist manifest (.m3u/.m3u8 HLS or media list, .mpd DASH).</summary>
    public static bool IsManifestFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return ManifestExtensions.Contains(Path.GetExtension(name));
    }

    /// <summary>True when the URL points at a playlist manifest.</summary>
    public static bool IsManifestExtension(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsManifestFileName(uri.AbsolutePath);

    /// <summary>What the probe learned about a direct link: the served media
    /// type and, when the server advertises one, the real file name
    /// (Content-Disposition).</summary>
    public sealed record DirectLinkProbe(string? ContentType, string? FileName);

    // Probe answers are memoized: routing probes a URL and the generic
    // downloader's analysis wants the same answer, so one URL costs one
    // network round trip. Signed download links expire, so successes age
    // out quickly; failures age out faster still so a transient outage or
    // a slow HEAD does not stick to the URL.
    private static readonly ConcurrentDictionary<string, (DateTime At, DirectLinkProbe? Probe)> ProbeMemo = new();
    private static readonly TimeSpan SuccessAge = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureAge = TimeSpan.FromMinutes(1);
    private const int MemoCapacity = 512;

    /// <summary>
    /// Probes the URL for its media type and real file name. HEAD goes first
    /// (the cheap form static file servers answer); a server that rejects or
    /// mishandles HEAD falls back to a one-byte Range GET, which succeeds
    /// wherever the actual download would. Answers are memoized per URL.
    /// Returns null only when the URL is unreachable - the download itself
    /// would then fail the same way.
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

        if (ProbeMemo.TryGetValue(url, out var hit)
            && DateTime.UtcNow - hit.At < (hit.Probe is null ? FailureAge : SuccessAge))
        {
            return hit.Probe;
        }

        var (contentType, fileName) = await ProbeWithAsync(uri, proxy, HttpMethod.Head, cancellationToken);
        if (contentType is null)
        {
            (contentType, fileName) = await ProbeWithAsync(uri, proxy, HttpMethod.Get, cancellationToken);
        }

        var probe = contentType is null ? null : new DirectLinkProbe(contentType, fileName);
        if (ProbeMemo.Count >= MemoCapacity)
        {
            ProbeMemo.Clear();
        }

        ProbeMemo[url] = (DateTime.UtcNow, probe);
        return probe;
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
