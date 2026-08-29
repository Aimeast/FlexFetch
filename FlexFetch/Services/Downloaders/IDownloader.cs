using FlexFetch.Entities;

namespace FlexFetch.Services.Downloaders;

/// <summary>A single playable/downloadable item discovered during analysis.</summary>
public sealed class MediaChild
{
    public string Url { get; init; } = string.Empty;

    public string? Title { get; init; }

    /// <summary>
    /// Downloader the parent analysis already resolved for this child (e.g.
    /// "Generic" for a direct media stream). When set, the child task uses
    /// exactly that downloader instead of re-negotiating via URL matching,
    /// Content-Type probing or the generic fallback chain.
    /// </summary>
    public string? DownloaderType { get; init; }
}

/// <summary>Result of analyzing a resource URL.</summary>
public sealed class AnalysisResult
{
    /// <summary>Extracted video/page title, used as the filename source.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Page/tweet text to display under the URL, if any.</summary>
    public string? ContentText { get; init; }

    /// <summary>Direct media URL when the link itself is a downloadable file.</summary>
    public string? DirectUrl { get; init; }

    /// <summary>Referrer page to send with the download request.</summary>
    public string? Referrer { get; init; }

    /// <summary>Filename suggested by the analysis (title-based).</summary>
    public string? SuggestedFileName { get; init; }

    /// <summary>Expanded children (e.g. playlist videos), empty for plain files.</summary>
    public IReadOnlyList<MediaChild> Children { get; init; } = Array.Empty<MediaChild>();
}

/// <summary>
/// A downloader plugin: declares supported resources, decides whether it can
/// handle a URL, analyzes it, and downloads the result.
/// </summary>
public interface IDownloader
{
    /// <summary>Unique downloader identifier (e.g. "Generic").</summary>
    string Type { get; }

    /// <summary>
    /// True for domain-specific downloaders (e.g. YouTube/Twitter); false for
    /// generic fallbacks (yt-dlp/Html/Browser/Generic). A URL matching more
    /// than one specific downloader is a runtime error.
    /// </summary>
    bool IsDomainSpecific { get; }

    /// <summary>Match rule: whether this downloader claims the URL.</summary>
    bool CanHandle(string url);

    /// <summary>Parses the URL and extracts title / direct link / children.</summary>
    Task<AnalysisResult> AnalyzeAsync(string url, string taskId, CancellationToken cancellationToken);

    /// <summary>Downloads the analyzed resource into the task's storage directory.</summary>
    Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken cancellationToken);
}
