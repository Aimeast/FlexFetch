using FlexFetch.Domain;
using FlexFetch.Services.Downloaders;
using Microsoft.Playwright;
using Serilog;
using System.Text.RegularExpressions;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services;

/// <summary>
/// Browser-assisted parsing downloader: renders a page in the stealth browser
/// (executing JavaScript) and extracts embedded video resources from the
/// rendered DOM. Used as the degradation step after plain-HTML analysis fails.
/// </summary>
public sealed partial class BrowserParsingDownloader : IDownloader
{
    private readonly StealthBrowserService _browser;
    private readonly ILogger _log;

    public BrowserParsingDownloader(StealthBrowserService browser, ILogger log)
    {
        _browser = browser;
        _log = log;
    }

    public string Type => "Browser";

    public int Priority => 30;

    public bool CanHandle(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public async Task<AnalysisResult> AnalyzeAsync(string url, CancellationToken cancellationToken)
    {
        var page = await _browser.NewPageAsync();
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30_000,
            });

            var title = await page.TitleAsync();
            var html = await page.ContentAsync();
            var mediaUrls = ExtractMediaUrls(html);

            _log.Information("Browser analysis of {Url}: {Count} media resources", url, mediaUrls.Count);
            return new AnalysisResult
            {
                Title = string.IsNullOrWhiteSpace(title) ? "web-video" : title,
                Referrer = url,
                Children = mediaUrls
                    .Select(u => new MediaChild { Url = u, Title = title })
                    .ToList(),
            };
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>Extracts media URLs from rendered HTML (pure logic, unit-testable).</summary>
    public static IReadOnlyList<string> ExtractMediaUrls(string html)
    {
        var urls = new List<string>();
        foreach (Match match in SourceRegex().Matches(html))
        {
            urls.Add(match.Groups[1].Value);
        }
        foreach (Match match in OgVideoRegex().Matches(html))
        {
            urls.Add(match.Groups[1].Value);
        }
        foreach (Match match in VideoUrlRegex().Matches(html))
        {
            urls.Add(match.Groups[1].Value);
        }

        return urls
            .Select(u => u.Trim())
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Browser analysis always expands children; nothing to download here.</summary>
    public Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Browser resources are expanded into child tasks, nothing to download directly");

    [GeneratedRegex(@"<(?:video|source|embed|iframe)[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRegex();

    [GeneratedRegex(@"<meta[^>]+property=""og:video(?::url)?""[^>]+content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgVideoRegex();

    [GeneratedRegex(@"<video[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex VideoUrlRegex();
}
