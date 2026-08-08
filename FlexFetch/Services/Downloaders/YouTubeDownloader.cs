using FlexFetch.Services.Routing;
using FlexFetch.Services;
using Serilog;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// YouTube downloader: yt-dlp with YouTube-specific URL matching.
/// Reuses the shared yt-dlp logic from <see cref="YtdlpDownloader"/>.
/// </summary>
public sealed class YouTubeDownloader : YtdlpDownloader
{
    public YouTubeDownloader(
        YtdlpService ytdlp,
        IProxyService proxy,
        StorageService storage,
        ILogger log,
        CookiePoolService cookies,
        Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>>? fetchData = null)
        : base(ytdlp, proxy, storage, log, cookies, fetchData)
    {
    }

    public override string Type => "YouTube";

    public override bool IsDomainSpecific => true;

    /// <summary>YouTube enables the cookie attach-and-retry on auth-class errors.</summary>
    protected override bool AuthRetryEnabled => true;

    public override bool CanHandle(string url) =>
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
}
