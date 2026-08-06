using FlexFetch.Domain;
using FlexFetch.Services.Downloaders;
using Serilog;
using System.Text.RegularExpressions;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services;

/// <summary>
/// Twitter/X downloader: fetches the status page, uses the og:description
/// (post text) as the filename source and expands embedded media URLs
/// (contentUrl) into child tasks that are downloaded with the page as Referrer.
/// </summary>
public sealed partial class TwitterDownloader : IDownloader
{
    private readonly IProxyService _proxy;
    private readonly ILogger _log;

    public TwitterDownloader(IProxyService proxy, ILogger log)
    {
        _proxy = proxy;
        _log = log;
    }

    public string Type => "Twitter";

    public int Priority => 90;

    public bool CanHandle(string url) =>
        url.Contains("x.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("twitter.com", StringComparison.OrdinalIgnoreCase);

    public async Task<AnalysisResult> AnalyzeAsync(string url, CancellationToken cancellationToken)
    {
        var pageUrl = new Uri(url);
        var handler = _proxy.CreateHandler(pageUrl);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        client.DefaultRequestHeaders.Referrer = new Uri("https://x.com/");

        var page = await client.GetStringAsync(pageUrl, cancellationToken);
        var description = DescriptionRegex().Match(page).Groups[1].Value;
        var title = string.IsNullOrWhiteSpace(description) ? "x-post" : description;

        var children = ContentUrlRegex().Matches(page)
            .Select(m => m.Groups[1].Value)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
            .Select(u => new MediaChild { Url = u, Title = title })
            .ToList();

        _log.Information("Twitter analysis of {Url}: {Count} media items", url, children.Count);
        return new AnalysisResult
        {
            Title = title,
            Referrer = url,
            Children = children,
        };
    }

    /// <summary>Twitter analysis always expands children; nothing to download here.</summary>
    public Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Twitter tasks are expanded into child tasks, nothing to download directly");

    [GeneratedRegex(@"<meta[^>]*property=""og:description""[^>]*content=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionRegex();

    [GeneratedRegex(@"content=""([^""]+)"".?itemProp=""contentUrl""", RegexOptions.Singleline)]
    private static partial Regex ContentUrlRegex();
}
