using FlexFetch.Services;
using FlexFetch.Services.Routing;
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
        Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>>? fetchData = null,
        Func<string, OptionSet, Action<double>, CancellationToken, Task<RunResult<string>>>? download = null)
        : base(ytdlp, proxy, storage, log, cookies, fetchData, download)
    {
    }

    public override string Type => "YouTube";

    public override bool IsDomainSpecific => true;

    /// <summary>YouTube enables the cookie attach-and-retry on auth-class errors.</summary>
    protected override bool AuthRetryEnabled => true;

    /// <summary>
    /// Builds the yt-dlp options and pins resilient YouTube player clients.
    /// A plain request from a datacenter IP is often answered with a bot
    /// challenge ("The page needs to be reloaded" / "Sign in to confirm you're
    /// not a bot"). web_safari additionally demands a po_token and fails with
    /// "The page needs to be reloaded" without one, so only clients that do
    /// not require a po_token are used (android, then tv, then mweb); the
    /// cookie attach-and-retry flow is unchanged.
    /// </summary>
    public override OptionSet BuildOptions(Uri url, string? outputPath = null, string? cookieFile = null)
    {
        var options = base.BuildOptions(url, outputPath, cookieFile);
        // Comma-separated list: yt-dlp picks the first client that works.
        // web_safari forces a po_token (Proof-of-Origin) challenge ("The page needs
        // to be reloaded") when the token is absent; android/tv/mweb do not require it
        // and bypass the bot check once cookies are attached.
        options.ExtractorArgs = "youtube:player_client=android,tv,mweb";
        return options;
    }

    public override bool CanHandle(string url) =>
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
}
