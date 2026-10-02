using FlexFetch.Entities;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Session;
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
/// logic are unit-testable. Output lines are always classified as a whole
/// before a verdict is drawn (an exit code alone is never trusted).
/// </summary>
public class YtdlpDownloader : IDownloader
{
    protected readonly YtdlpService Ytdlp;
    protected readonly IProxyService Proxy;
    protected readonly StorageService Storage;
    protected readonly ILogger Log;

    private readonly Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>> _fetchData;
    private readonly Func<string, OptionSet, Action<double>, CancellationToken, Task<RunResult<string>>> _downloadVideo;

    public YtdlpDownloader(
        YtdlpService ytdlp,
        IProxyService proxy,
        StorageService storage,
        ILogger log,
        Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>>? fetchData = null,
        Func<string, OptionSet, Action<double>, CancellationToken, Task<RunResult<string>>>? download = null)
    {
        Ytdlp = ytdlp;
        Proxy = proxy;
        Storage = storage;
        Log = log;
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
            options.Proxy = NormalizeProxyForYtdlp(proxyUri);
        }

        return options;
    }

    /// <summary>
    /// yt-dlp must resolve DNS through the proxy (socks5h): a local lookup
    /// can resolve to a poisoned or unreachable address, defeating the fixed
    /// single egress. The browser path already does remote DNS natively.
    /// </summary>
    public static string NormalizeProxyForYtdlp(string proxyUri) =>
        proxyUri.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)
            ? "socks5h://" + proxyUri["socks5://".Length..]
            : proxyUri;

    /// <summary>
    /// Placeholder titles yt-dlp reports for playlist entries the site hides
    /// as unplayable (private, deleted, region/age blocked). They can never
    /// be downloaded and must not become child tasks.
    /// </summary>
    private static readonly HashSet<string> UnavailablePlaceholders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "[Private video]",
            "[Deleted video]",
            "[Unavailable video]",
            "[Unavailable]",
        };

    /// <summary>Converts fetched video metadata into an analysis result (title + children).</summary>
    public AnalysisResult BuildAnalysis(VideoData data)
    {
        if (data.Entries is { Length: > 0 })
        {
            // Skip entries without a URL or a title: unavailable/dead videos
            // (e.g. terminated accounts) still appear in playlist output but
            // would only fail during the child download. Placeholder titles
            // ("[Private video]" and friends) are the site's hidden videos -
            // filtered the same way so they never enter the download queue.
            var children = data.Entries
                .Where(e => !string.IsNullOrEmpty(e.Url) && !string.IsNullOrEmpty(e.Title))
                .Where(e => !UnavailablePlaceholders.Contains(e.Title.Trim()))
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

    public virtual async Task<AnalysisResult> AnalyzeAsync(string url, string taskId, CancellationToken cancellationToken)
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
        string? cookieFile = null;
        try
        {
            if (!result.Success || result.Data is null)
            {
                // Anonymous first: only an auth-class failure makes a session
                // worth attaching (subclass hook).
                if (ShouldRetryWithSession(result.ErrorOutput))
                {
                    cookieFile = await AcquireSessionCookieFileAsync(taskId, cancellationToken);
                    if (cookieFile is not null)
                    {
                        ApplySessionPosture(options, cookieFile);
                        Log.Information("Task {TaskId}: retrying with session cookies ({CookieFile})",
                            taskId, Path.GetFileName(cookieFile));
                        result = await _fetchData(url, options, cancellationToken);
                    }
                }

                if (!result.Success || result.Data is null)
                {
                    throw BuildFailure(result.ErrorOutput, "yt-dlp failed");
                }
            }

            ExamineSuccessOutput(result.ErrorOutput, taskId);
        }
        finally
        {
            // A session cookie copy is consumed by exactly one run and never
            // merged back: yt-dlp rewrites the file it is given, and a
            // blocked run would poison the jar.
            DeleteCookieFileIfAny(cookieFile);
        }

        return BuildAnalysis(result.Data);
    }

    public virtual async Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken cancellationToken)
    {
        // Prefer the analyzed file name (title + extension) so downloads keep
        // their extension; the stored name is only a fallback for cases where
        // analysis produced no suggestion.
        var outputPath = Storage.GetTaskDir(task.Id) + Path.DirectorySeparatorChar
            + (analysis.SuggestedFileName ?? task.FileName ?? "video.mp4");
        var options = BuildOptions(new Uri(task.Url), outputPath);

        // yt-dlp repaints its progress bar with carriage returns on a single
        // line when piped; the process line reader only fires on \n, so
        // without --newline every update would buffer until the download
        // ends and the task page would show no progress at all.
        options.Progress = true;
        options.Newline = true;

        // The file name is decided before the first byte is fetched: show it
        // in the task list during the download (it is persisted together
        // with the first progress update), not only after completion.
        task.FileName = Path.GetFileName(outputPath);

        var result = await _downloadVideo(task.Url, options, progress, cancellationToken);
        string? cookieFile = null;
        try
        {
            if (!result.Success && ShouldRetryWithSession(result.ErrorOutput))
            {
                cookieFile = await AcquireSessionCookieFileAsync(task.Id, cancellationToken);
                if (cookieFile is not null)
                {
                    ApplySessionPosture(options, cookieFile);
                    Log.Information("Task {TaskId}: retrying download with session cookies ({CookieFile})",
                        task.Id, Path.GetFileName(cookieFile));
                    result = await _downloadVideo(task.Url, options, progress, cancellationToken);
                }
            }

            if (!result.Success)
            {
                throw BuildFailure(result.ErrorOutput, "yt-dlp download failed");
            }

            ExamineSuccessOutput(result.ErrorOutput, task.Id);
            // Prefer the path yt-dlp reports after its move/merge step
            // ("outfile:"): a merged container can differ from the -o
            // template (e.g. webm fragments merged into mp4).
            var finalPath = string.IsNullOrWhiteSpace(result.Data) ? outputPath : result.Data;
            task.FileName = Path.GetFileName(finalPath);
            task.FileSize = File.Exists(finalPath) ? new FileInfo(finalPath).Length : null;
        }
        finally
        {
            DeleteCookieFileIfAny(cookieFile);
        }
    }

    // --- Session retry hooks (only the YouTube downloader enables them) ---

    /// <summary>True when the downloader may attach a session copy and retry
    /// once on auth-class failures.</summary>
    protected virtual bool SessionRetryEnabled => false;

    /// <summary>
    /// True when the classified failure can still be fixed by a valid
    /// session (bot check / login / age / private / members).
    /// </summary>
    protected bool ShouldRetryWithSession(IReadOnlyList<string> errorOutput) =>
        SessionRetryEnabled && YtdlpOutputClassifier.IsAuthFamily(
            YtdlpOutputClassifier.Classify(errorOutput, runSucceeded: false));

    /// <summary>Returns a one-time cookie file for the run, or null when no
    /// session is available. The implementation owns cleanup ownership.</summary>
    protected virtual Task<string?> AcquireSessionCookieFileAsync(string taskId, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>Applies the authenticated posture on top of the options
    /// (cookie file, client set, visitor identity pairing).</summary>
    protected virtual void ApplySessionPosture(OptionSet options, string cookieFile)
    {
        options.Cookies = cookieFile;
    }

    /// <summary>Inspects the output of a SUCCESSFUL run: a rotated-session
    /// WARNING still means the jar is dying (yt-dlp fell back to anonymous)
    /// and must trigger a re-export even at exit code 0.</summary>
    protected virtual void OnSessionRotated(IReadOnlyList<string> output, string taskId)
    {
    }

    private void ExamineSuccessOutput(IReadOnlyList<string> output, string taskId)
    {
        if (output.Count == 0)
        {
            return;
        }

        var cls = YtdlpOutputClassifier.Classify(output, runSucceeded: true);
        if (cls == YtdlpOutputClass.SessionRotated)
        {
            Log.Warning("Task {TaskId}: session cookies were rotated during a successful run; triggering re-export", taskId);
            OnSessionRotated(output, taskId);
        }
    }

    private static void DeleteCookieFileIfAny(string? cookieFile)
    {
        try
        {
            if (cookieFile is not null && File.Exists(cookieFile))
            {
                File.Delete(cookieFile);
            }
        }
        catch
        {
            // Cleanup is best-effort; a leftover copy is harmless.
        }
    }

    private static Exception BuildFailure(IReadOnlyList<string> errorOutput, string prefix)
    {
        var message = BuildFailureMessage(errorOutput, prefix);
        var cls = YtdlpOutputClassifier.Classify(errorOutput, runSucceeded: false);
        return cls switch
        {
            YtdlpOutputClass.Retryable => new RetryableException(message),
            // Precise reason for the task record; fails immediately either way.
            YtdlpOutputClass.BotCheck => new AuthRequiredException(AuthFailureReason.LoginRequired, message),
            YtdlpOutputClass.LoginRequired => new AuthRequiredException(AuthFailureReason.LoginRequired, message),
            YtdlpOutputClass.AgeRestricted => new AuthRequiredException(AuthFailureReason.AgeRestricted, message),
            YtdlpOutputClass.Private => new AuthRequiredException(AuthFailureReason.Private, message),
            YtdlpOutputClass.MembersOnly => new AuthRequiredException(AuthFailureReason.MembersOnly, message),
            // SessionRotated / JarInconsistent / DeadContent / Unknown: no
            // point retrying the task; a rotated jar additionally triggered
            // the re-export path via ExamineSuccessOutput when relevant.
            _ => new InvalidOperationException(message),
        };
    }

    private static string BuildFailureMessage(IReadOnlyList<string> errorOutput, string prefix) =>
        $"{prefix}: {string.Join(';', errorOutput.Where(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)).Take(3))}";

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
