using System.Collections.Concurrent;
using FlexFetch.Entities;
using FlexFetch.Services.Session;
using Microsoft.Playwright;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Browser-assisted media detection: renders a page in a throw-away headless
/// Firefox and SNIFFS the network activity (responses) for media streams -
/// video/audio content types, HLS/DASH manifests - instead of parsing the
/// loaded HTML (which HtmlResourceDetector already does). Discovered media
/// URLs become child tasks. Used as the degradation step after plain-HTML
/// analysis fails. The browser is freshly launched per analysis and never
/// touches the session profile.
/// </summary>
public sealed class BrowserParsingDownloader : IDownloader
{
    private readonly FirefoxBrowserService _browser;
    private readonly ILogger _log;

    public BrowserParsingDownloader(FirefoxBrowserService browser, ILogger log)
    {
        _browser = browser;
        _log = log;
    }

    public string Type => "Browser";

    public bool IsDomainSpecific => false;

    public bool CanHandle(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public async Task<AnalysisResult> AnalyzeAsync(string url, string taskId, CancellationToken cancellationToken)
    {
        await using var ephemeral = await _browser.OpenEphemeralPageAsync(url);
        var page = ephemeral.Page;
        var mediaUrls = new ConcurrentBag<string>();

        void OnResponse(object? sender, IResponse response)
        {
            if (IsMediaResponse(response.Headers, response.Url))
            {
                mediaUrls.Add(response.Url);
            }
        }

        page.Response += OnResponse;
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30_000,
            });

            // Give the player a moment to request media streams after load.
            await page.WaitForTimeoutAsync(5_000);
        }
        finally
        {
            page.Response -= OnResponse;
        }

        var title = string.IsNullOrWhiteSpace(await page.TitleAsync()) ? "web-video" : await page.TitleAsync();
        var children = mediaUrls
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(u => new MediaChild { Url = u, Title = title })
            .ToList();

        _log.Information("Browser sniffing of {Url}: {Count} media responses", url, children.Count);
        return new AnalysisResult
        {
            Title = title,
            Referrer = url,
            Children = children,
        };
    }

    /// <summary>
    /// Classifies a network response as media based on Content-Type (video/*,
    /// audio/*, HLS/DASH manifests) with a URL-extension fallback.
    /// Pure logic, unit-testable.
    /// </summary>
    public static bool IsMediaResponse(IReadOnlyDictionary<string, string> headers, string url)
    {
        if (headers.TryGetValue("Content-Type", out var contentType))
        {
            var ct = contentType.ToLowerInvariant();
            if (ct.StartsWith("video/", StringComparison.Ordinal)
                || ct.StartsWith("audio/", StringComparison.Ordinal))
            {
                return true;
            }

            // HLS / DASH manifests.
            if (ct.Contains("mpegurl") || ct.Contains("mpd"))
            {
                return true;
            }

            // Unknown binary often serves media; fall back to the extension.
            if (ct.Contains("octet-stream") && HasMediaExtension(url))
            {
                return true;
            }
        }

        return HasMediaExtension(url);
    }

    /// <summary>True when the URL path ends with a common media extension.</summary>
    public static bool HasMediaExtension(string url) => DirectLinkDetector.HasMediaExtension(url);

    /// <summary>Browser sniffing always expands children; nothing to download here.</summary>
    public Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Browser resources are expanded into child tasks, nothing to download directly");
}
