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
    protected readonly ILogger Log;

    private readonly Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>> _fetchData;

    public YtdlpDownloader(
        YtdlpService ytdlp,
        IProxyService proxy,
        StorageService storage,
        ILogger log,
        Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>>? fetchData = null)
    {
        Ytdlp = ytdlp;
        Proxy = proxy;
        Storage = storage;
        Log = log;
        _fetchData = fetchData ?? DefaultFetchDataAsync;
    }

    public virtual string Type => "Ytdlp";

    public virtual int Priority => 80;

    public virtual bool CanHandle(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the yt-dlp option set for a fetch/download call.</summary>
    public OptionSet BuildOptions(Uri url, string? outputPath = null)
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
            var children = data.Entries
                .Where(e => !string.IsNullOrEmpty(e.Url))
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

    public async Task<AnalysisResult> AnalyzeAsync(string url, CancellationToken cancellationToken)
    {
        var target = new Uri(url);
        var options = BuildOptions(target);
        var result = await _fetchData(url, options, cancellationToken);
        if (!result.Success || result.Data is null)
        {
            throw new InvalidOperationException(
                $"yt-dlp failed: {string.Join(';', result.ErrorOutput.Where(l => l.StartsWith("ERROR:")).Take(3))}");
        }

        return BuildAnalysis(result.Data);
    }

    public async Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken cancellationToken)
    {
        // Ensure yt-dlp is available before using it (lazy install).
        await Ytdlp.EnsureInstalledAsync(cancellationToken);

        var ytdlp = new YoutubeDL { YoutubeDLPath = Ytdlp.BinaryPath };
        var outputPath = Storage.GetTaskDir(task.Id) + Path.DirectorySeparatorChar
            + (task.FileName ?? analysis.SuggestedFileName ?? "video.mp4");
        var options = BuildOptions(new Uri(task.Url), outputPath);

        var dlProgress = new Progress<DownloadProgress>(p =>
        {
            // Progress is a percentage (0-100); normalize to 0..1.
            progress(Math.Clamp(p.Progress / 100f, 0f, 1f));
        });

        var result = await ytdlp.RunVideoDownload(
            task.Url,
            ct: cancellationToken,
            progress: dlProgress,
            overrideOptions: options);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"yt-dlp download failed: {string.Join(';', result.ErrorOutput.Where(l => l.StartsWith("ERROR:")).Take(3))}");
        }

        task.FileName = Path.GetFileName(outputPath);
        task.FileSize = File.Exists(outputPath) ? new FileInfo(outputPath).Length : null;
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
        return await ytdlp.RunVideoDataFetch(url, ct: cancellationToken, overrideOptions: options);
    }
}
