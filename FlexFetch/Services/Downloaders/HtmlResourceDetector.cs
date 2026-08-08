using System.Text.RegularExpressions;
using FlexFetch.Entities;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Browser-less fallback analysis for arbitrary web pages: renders nothing,
/// but extracts embedded video resources from the raw HTML (direct video
/// files, &lt;video&gt; sources, og:video meta). Discovered resources become
/// child tasks; a direct video URL is downloaded directly.
/// </summary>
public sealed partial class HtmlResourceDetector : IDownloader
{
    private readonly IProxyService _proxy;
    private readonly ILogger _log;

    public HtmlResourceDetector(IProxyService proxy, ILogger log)
    {
        _proxy = proxy;
        _log = log;
    }

    public string Type => "Html";

    public bool IsDomainSpecific => false;

    public bool CanHandle(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public async Task<AnalysisResult> AnalyzeAsync(string url, string taskId, CancellationToken cancellationToken)
    {
        var pageUrl = new Uri(url);
        var handler = _proxy.CreateHandler(pageUrl);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        var page = await client.GetStringAsync(pageUrl, cancellationToken);
        var title = TitleRegex().Match(page).Groups[1].Value.Trim();

        var mediaUrls = new List<string>();
        foreach (Match match in SourceRegex().Matches(page))
        {
            mediaUrls.Add(match.Groups[1].Value);
        }
        foreach (Match match in OgVideoRegex().Matches(page))
        {
            mediaUrls.Add(match.Groups[1].Value);
        }

        // Resolve relative URLs against the page URL; keep only valid absolute URLs.
        var children = new List<MediaChild>();
        foreach (var raw in mediaUrls)
        {
            var u = raw.Trim();
            if (Uri.TryCreate(u, UriKind.Absolute, out var absolute))
            {
                children.Add(new MediaChild { Url = absolute.ToString(), Title = title });
            }
            else if (Uri.TryCreate(pageUrl, u, out var resolved))
            {
                children.Add(new MediaChild { Url = resolved.ToString(), Title = title });
            }
        }

        children = children
            .DistinctBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _log.Information("Html analysis of {Url}: {Count} media resources", url, children.Count);
        return new AnalysisResult
        {
            Title = string.IsNullOrWhiteSpace(title) ? "web-video" : title,
            Referrer = url,
            Children = children,
        };
    }

    /// <summary>Html analysis always expands children; nothing to download here.</summary>
    public Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Html resources are expanded into child tasks, nothing to download directly");

    [GeneratedRegex(@"<title[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<(?:video|source|embed|iframe)[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRegex();

    [GeneratedRegex(@"<meta[^>]+property=""og:video(?::url)?""[^>]+content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgVideoRegex();
}
