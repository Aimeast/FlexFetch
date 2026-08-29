using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Routing;
using Serilog;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Generic yt-dlp based downloader: fallback for any video site supported by
/// yt-dlp (vimeo, dailymotion, ...). Single videos and playlist
/// expansion, audio/video merging via ffmpeg, title-based file naming.
/// The yt-dlp fetch call is injectable so parameter building and expansion
/// logic are unit-testable.
/// </summary>
public class YtdlpDownloader : IDownloader
{
    protected readonly YtdlpService Ytdlp;
    protected readonly IProxyService Proxy;
    protected readonly StorageService Storage;
    protected readonly CookiePoolService Cookies;
    protected readonly ILogger Log;

    private readonly Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>> _fetchData;
    private readonly Func<string, OptionSet, Action<double>, CancellationToken, Task<RunResult<string>>> _downloadVideo;

    public YtdlpDownloader(
        YtdlpService ytdlp,
        IProxyService proxy,
        StorageService storage,
        ILogger log,
        CookiePoolService cookies,
        Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>>? fetchData = null,
        Func<string, OptionSet, Action<double>, CancellationToken, Task<RunResult<string>>>? download = null)
    {
        Ytdlp = ytdlp;
        Proxy = proxy;
        Storage = storage;
        Log = log;
        Cookies = cookies;
        _fetchData = fetchData ?? DefaultFetchDataAsync;
        _downloadVideo = download ?? DefaultDownloadVideoAsync;
    }

    public virtual string Type => "Ytdlp";

    public virtual bool IsDomainSpecific => false;

    public virtual bool CanHandle(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the yt-dlp option set for a fetch/download call.</summary>
    public virtual OptionSet BuildOptions(Uri url, string? outputPath = null, string? cookieFile = null)
    {
        var options = new OptionSet
        {
            Format = "bestvideo+bestaudio/best",
            FormatSort = "vcodec:h264:h265:av01:vp9:vp9.2",
            MergeOutputFormat = DownloadMergeFormat.Mp4,
        };
        if (outputPath is not null)
        {
            options.Output = outputPath;
        }

        if (cookieFile is not null)
        {
            options.Cookies = cookieFile;
        }

        var proxyUri = Proxy.GetProxyUri(url);
        if (proxyUri is not null)
        {
            options.Proxy = proxyUri;
        }

        return options;
    }

    /// <summary>Converts fetched video metadata into an analysis result (title + children).</summary>
    public AnalysisResult BuildAnalysis(VideoData data)
    {
        if (data.Entries is { Length: > 0 })
        {
            // Skip entries without a URL or a title: unavailable/dead videos
            // (e.g. terminated accounts) still appear in playlist output but
            // would only fail during the child download.
            var children = data.Entries
                .Where(e => !string.IsNullOrEmpty(e.Url) && !string.IsNullOrEmpty(e.Title))
                .Select(e => new MediaChild { Url = e.Url, Title = e.Title })
                .ToList();
            return new AnalysisResult
            {
                Title = data.Title ?? string.Empty,
                Children = children,
            };
        }

        var fileName = $"{SanitizeTitle(data.Title)}.{data.Extension}";
        return new AnalysisResult
        {
            Title = data.Title ?? string.Empty,
            DirectUrl = data.Url ?? data.WebpageUrl,
            SuggestedFileName = fileName,
        };
    }

    public async Task<AnalysisResult> AnalyzeAsync(string url, string taskId, CancellationToken cancellationToken)
    {
        var target = new Uri(url);
        var options = BuildOptions(target);
        // Playlist manifests (.m3u lists) can hold thousands of entries: fetch
        // them flat so analysis does not deep-extract every entry. HLS media
        // and master playlists are single videos and unaffected by the flag.
        if (DirectLinkDetector.IsManifestExtension(url))
        {
            options.FlatPlaylist = true;
        }

        var result = await _fetchData(url, options, cancellationToken);
        if (!result.Success || result.Data is null)
        {
            result = await RetryWithCookiesIfAuthAsync(url, taskId, options, result.ErrorOutput, cancellationToken)
                ?? throw BuildFailure(result.ErrorOutput, "yt-dlp failed");
        }

        return BuildAnalysis(result.Data);
    }

    /// <summary>
    /// For auth-class yt-dlp errors (only when the downloader enables it):
    /// attaches pool cookies once and retries; returns the retried result, or
    /// null when the error is not auth-class or no cookies are available.
    /// When cookies were attached and the retry still fails, an
    /// AuthRequiredException is thrown instead.
    /// </summary>
    private async Task<RunResult<VideoData>?> RetryWithCookiesIfAuthAsync(
        string url,
        string taskId,
        OptionSet options,
        IReadOnlyList<string> errorOutput,
        CancellationToken cancellationToken)
    {
        if (!AuthRetryEnabled || !TryClassifyAuthError(errorOutput, out var reason))
        {
            return null;
        }

        var cookieFile = AttachCookiesIfAny(url, taskId);
        if (cookieFile is null)
        {
            throw new AuthRequiredException(reason, BuildFailureMessage(errorOutput, "yt-dlp failed"));
        }

        try
        {
            options.Cookies = cookieFile;
            var retried = await _fetchData(url, options, cancellationToken);
            if (!retried.Success || retried.Data is null)
            {
                throw new AuthRequiredException(
                    reason, BuildFailureMessage(retried.ErrorOutput, "yt-dlp failed with cookies"));
            }

            return retried;
        }
        finally
        {
            WriteBackCookiesAndCleanup(cookieFile, taskId);
        }
    }

    /// <summary>
    /// True when the downloader should attach pool cookies and retry once on
    /// auth-class errors (only YouTube enables this).
    /// </summary>
    protected virtual bool AuthRetryEnabled => false;

    private string? AttachCookiesIfAny(string url, string taskId)
    {
        var cookies = Cookies.GetCookiesForUrl(new Uri(url));
        if (cookies.Count == 0)
        {
            return null;
        }

        var domains = cookies.Select(c => c.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Log.Information("Task {TaskId}: attaching {Count} cookies for {Url} (domains: {Domains})",
            taskId, cookies.Count, url, string.Join(", ", domains));

        CookieFile.Write(Storage.DataDir, taskId, cookies);
        return CookieFile.GetPath(Storage.DataDir, taskId);
    }

    private void WriteBackCookiesAndCleanup(string cookieFile, string taskId)
    {
        var updated = CookieFile.Read(cookieFile);
        if (updated.Count > 0)
        {
            Cookies.UpsertCookies(updated);
        }

        CookieFile.Delete(Storage.DataDir, taskId);
    }

    private static Exception BuildFailure(IReadOnlyList<string> errorOutput, string prefix)
    {
        var message = BuildFailureMessage(errorOutput, prefix);
        return TryClassifyRetryableError(errorOutput)
            ? new RetryableException(message)
            : new InvalidOperationException(message);
    }

    /// <summary>
    /// True when the yt-dlp error indicates a transient failure worth
    /// retrying (timeout, connection failure, proxy fault, server 5xx, rate
    /// limiting). Deterministic errors are not classified here.
    /// </summary>
    private static bool TryClassifyRetryableError(IReadOnlyList<string> errorOutput)
    {
        foreach (var line in errorOutput)
        {
            if (!line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || line.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || line.Contains("unable to connect", StringComparison.OrdinalIgnoreCase)
                || line.Contains("connection error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed to establish", StringComparison.OrdinalIgnoreCase)
                || line.Contains("could not connect", StringComparison.OrdinalIgnoreCase)
                || line.Contains("socks", StringComparison.OrdinalIgnoreCase)
                || line.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
                || line.Contains("http error 429", StringComparison.OrdinalIgnoreCase)
                || line.Contains("http error 5", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildFailureMessage(IReadOnlyList<string> errorOutput, string prefix) =>
        $"{prefix}: {string.Join(';', errorOutput.Where(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)).Take(3))}";

    /// <summary>
    /// Classifies yt-dlp ERROR lines that indicate authentication/restriction.
    /// Matching deliberately avoids the apostrophe inside "you're" (its
    /// encoding is unstable across environments), using the stable substrings
    /// "sign in to confirm" and "not a bot" instead.
    /// </summary>
    private static bool TryClassifyAuthError(IReadOnlyList<string> errorOutput, out AuthFailureReason reason)
    {
        reason = AuthFailureReason.Unknown;
        foreach (var line in errorOutput)
        {
            if (!line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Contains("sign in to confirm", StringComparison.OrdinalIgnoreCase)
                || line.Contains("not a bot", StringComparison.OrdinalIgnoreCase)
                || line.Contains("login_required", StringComparison.OrdinalIgnoreCase)
                || line.Contains("login required", StringComparison.OrdinalIgnoreCase)
                || line.Contains("please sign in", StringComparison.OrdinalIgnoreCase)
                || line.Contains("--cookies", StringComparison.OrdinalIgnoreCase))
            {
                reason = AuthFailureReason.LoginRequired;
                return true;
            }

            if (line.Contains("private video", StringComparison.OrdinalIgnoreCase))
            {
                reason = AuthFailureReason.Private;
                return true;
            }

            if (line.Contains("age-restricted", StringComparison.OrdinalIgnoreCase)
                || line.Contains("age restricted", StringComparison.OrdinalIgnoreCase))
            {
                reason = AuthFailureReason.AgeRestricted;
                return true;
            }

            if (line.Contains("members only", StringComparison.OrdinalIgnoreCase)
                || line.Contains("member-only", StringComparison.OrdinalIgnoreCase))
            {
                reason = AuthFailureReason.MembersOnly;
                return true;
            }
        }

        return false;
    }

    public async Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken cancellationToken)
    {
        // Prefer the analyzed file name (title + extension) so downloads keep
        // their extension; the stored name is only a fallback for cases where
        // analysis produced no suggestion.
        var outputPath = Storage.GetTaskDir(task.Id) + Path.DirectorySeparatorChar
            + (analysis.SuggestedFileName ?? task.FileName ?? "video.mp4");
        var options = BuildOptions(new Uri(task.Url), outputPath);

        string? cookieFile = null;
        try
        {
            // Attach pool cookies up front: the analysis phase already used
            // them, so the download must carry the same authenticated state.
            cookieFile = AttachCookiesIfAny(task.Url, task.Id);
            if (cookieFile is not null)
            {
                options.Cookies = cookieFile;
            }

            var result = await _downloadVideo(task.Url, options, progress, cancellationToken);
            if (!result.Success && cookieFile is null && TryClassifyAuthError(result.ErrorOutput, out var reason))
            {
                // First attempt had no cookies; retry once with the pool cookies.
                cookieFile = AttachCookiesIfAny(task.Url, task.Id);
                if (cookieFile is not null)
                {
                    options.Cookies = cookieFile;
                    result = await _downloadVideo(task.Url, options, progress, cancellationToken);
                }

                if (!result.Success)
                {
                    throw new AuthRequiredException(
                        reason, BuildFailureMessage(result.ErrorOutput, "yt-dlp download failed with cookies"));
                }
            }

            if (!result.Success)
            {
                if (TryClassifyAuthError(result.ErrorOutput, out var authReason))
                {
                    throw new AuthRequiredException(
                        authReason, BuildFailureMessage(result.ErrorOutput, "yt-dlp download failed"));
                }

                throw BuildFailure(result.ErrorOutput, "yt-dlp download failed");
            }

            task.FileName = Path.GetFileName(outputPath);
            task.FileSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : null;
        }
        finally
        {
            if (cookieFile is not null)
            {
                WriteBackCookiesAndCleanup(cookieFile, task.Id);
            }
        }
    }

    /// <summary>Runs the real yt-dlp binary to download the video (injectable in tests).</summary>
    private async Task<RunResult<string>> DefaultDownloadVideoAsync(
        string url,
        OptionSet options,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        // Ensure yt-dlp is available before invoking the real binary
        // (lazy install; tests inject a fake download delegate and skip this).
        await Ytdlp.EnsureInstalledAsync(cancellationToken);

        var ytdlp = new YoutubeDL { YoutubeDLPath = Ytdlp.BinaryPath };
        if (File.Exists(Ytdlp.FfmpegPath))
        {
            // Prefer the bundled ffmpeg (kept up to date by the upgrade
            // command); fall back to the system PATH one when absent.
            ytdlp.FFmpegPath = Ytdlp.FfmpegPath;
        }

        var dlProgress = new Progress<DownloadProgress>(p =>
        {
            // Progress is a percentage (0-100); normalize to 0..1.
            progress(Math.Clamp(p.Progress / 100f, 0f, 1f));
        });

        return await ytdlp.RunVideoDownload(
            url,
            ct: cancellationToken,
            progress: dlProgress,
            overrideOptions: options);
    }

    protected static string SanitizeTitle(string? title)
    {
        var cleaned = string.IsNullOrWhiteSpace(title) ? "video" : title.Trim();
        return FileNameRules.Sanitize(cleaned);
    }

    private async Task<RunResult<VideoData>> DefaultFetchDataAsync(
        string url,
        OptionSet options,
        CancellationToken cancellationToken)
    {
        // Ensure yt-dlp is available before invoking the real binary
        // (lazy install; tests inject a fake fetch delegate and skip this).
        await Ytdlp.EnsureInstalledAsync(cancellationToken);

        var ytdlp = new YoutubeDL { YoutubeDLPath = Ytdlp.BinaryPath };
        if (File.Exists(Ytdlp.FfmpegPath))
        {
            // Prefer the bundled ffmpeg (kept up to date by the upgrade
            // command); fall back to the system PATH one when absent.
            ytdlp.FFmpegPath = Ytdlp.FfmpegPath;
        }

        return await ytdlp.RunVideoDataFetch(url, ct: cancellationToken, overrideOptions: options);
    }
}
