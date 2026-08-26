using FlexFetch.Data;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using Serilog;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class YtdlpDownloaderTests
{
    private static readonly ILogger Log = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();

    private static YtdlpDownloader CreateDownloader(Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>>? fetch = null)
    {
        var dir = TestApp.CreateTempDataDir();
        var proxy = new DirectProxyService();
        var ytdlp = new YtdlpService(proxy, Log, dir);
        var storage = new StorageService(dir);
        var store = new LiteDbStore(Path.Combine(dir, "flexfetch.db"));
        var cookies = new CookiePoolService(new CookieRepository(store), () => new ICookieDomainMapping[] { new DefaultCookieDomainMapping() });
        return new YtdlpDownloader(ytdlp, proxy, storage, Log, cookies, fetch);
    }

    [TestMethod]
    public void CanHandle_AnyHttpUrl()
    {
        var downloader = CreateDownloader();

        Assert.IsTrue(downloader.CanHandle("https://www.example.com/video/xyz789"));
        Assert.IsTrue(downloader.CanHandle("https://vimeo.com/123456"));
        Assert.IsTrue(downloader.CanHandle("https://www.dailymotion.com/video/x8abc"));
        Assert.IsFalse(downloader.CanHandle("ftp://example.com/file"));
    }

    [TestMethod]
    public void IsDomainSpecific_FalseForGenericChain()
    {
        var downloader = CreateDownloader();
        var ytDir = TestApp.CreateTempDataDir();
        var ytStore = new LiteDbStore(Path.Combine(ytDir, "flexfetch.db"));
        var youtube = new YouTubeDownloader(
            new YtdlpService(new DirectProxyService(), Log, ytDir),
            new DirectProxyService(),
            new StorageService(ytDir),
            Log,
            new CookiePoolService(new CookieRepository(ytStore), () => new ICookieDomainMapping[] { new DefaultCookieDomainMapping() }));

        // YtdlpDownloader is part of the generic fallback chain; YouTube is
        // a domain-specific downloader used alone.
        Assert.IsFalse(downloader.IsDomainSpecific);
        Assert.IsTrue(youtube.IsDomainSpecific);
    }

    [TestMethod]
    public void BuildAnalysis_SingleVideo_SuggestsTitleFileName()
    {
        var downloader = CreateDownloader();
        var data = new VideoData
        {
            Title = "Sample Clip",
            Extension = "mp4",
            Url = "https://example.com/v.mp4",
        };

        var analysis = downloader.BuildAnalysis(data);

        Assert.AreEqual("Sample Clip", analysis.Title);
        Assert.IsEmpty(analysis.Children);
        Assert.AreEqual("Sample Clip.mp4", analysis.SuggestedFileName);
    }

    [TestMethod]
    public void ExtractFfmpegVersionNumber_ParsesStableVersion()
    {
        Assert.AreEqual("7.1", YtdlpService.ExtractFfmpegVersionNumber(
            "ffmpeg version 7.1-essentials_build-www.gyan.dev Copyright (c) 2000-2025 the FFmpeg developers"));
        // Patch-level versions (9.0.1) must not be truncated to 9.0.
        Assert.AreEqual("9.0.1", YtdlpService.ExtractFfmpegVersionNumber(
            "ffmpeg version 9.0.1-essentials_build-www.gyan.dev Copyright (c) 2000-2026 the FFmpeg developers"));
        Assert.IsNull(YtdlpService.ExtractFfmpegVersionNumber(null));
        Assert.IsNull(YtdlpService.ExtractFfmpegVersionNumber("garbage output"));
    }

    [TestMethod]
    public void IsFfmpegUpToDate_ComparesInstalledVsLatest()
    {
        const string latest = "7.1";
        var installed = "ffmpeg version 7.1-essentials_build-www.gyan.dev Copyright (c) 2000-2025 the FFmpeg developers";

        Assert.IsTrue(YtdlpService.IsFfmpegUpToDate(installed, latest));
        Assert.IsFalse(YtdlpService.IsFfmpegUpToDate("ffmpeg version 7.0-essentials_build", latest));
        Assert.IsFalse(YtdlpService.IsFfmpegUpToDate(null, latest));

        // 9.0.1 vs 9.0.1: equal (the earlier truncation bug made this false).
        Assert.IsTrue(YtdlpService.IsFfmpegUpToDate(
            "ffmpeg version 9.0.1-essentials_build-www.gyan.dev", "9.0.1"));
        Assert.IsFalse(YtdlpService.IsFfmpegUpToDate(
            "ffmpeg version 9.0-essentials_build-www.gyan.dev", "9.0.1"));
    }

    [TestMethod]
    public async Task UpgradeFfmpeg_RemoteQueryFailure_KeepsInstalledBinary()
    {
        var dir = TestApp.CreateTempDataDir();
        var components = Path.Combine(dir, "components");
        Directory.CreateDirectory(components);
        var ffmpegPath = Path.Combine(components, "ffmpeg.exe");
        File.WriteAllBytes(ffmpegPath, new byte[] { 1, 2, 3 });

        var proxy = new DirectProxyService();
        // The remote version query fails: the upgrade must not download
        // blindly and must leave the installed binary untouched.
        var service = new YtdlpService(
            proxy, Log, dir,
            _ => throw new InvalidOperationException("network down"));

        await service.UpgradeFfmpegAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(ffmpegPath));
    }

    [TestMethod]
    public void BuildAnalysis_Playlist_ExpandsChildren()
    {
        var downloader = CreateDownloader();
        var data = new VideoData
        {
            Title = "Collection",
            Entries = new[]
            {
                new VideoData { Title = "One", Url = "https://example.com/1" },
                new VideoData { Title = "Two", Url = "https://example.com/2" },
            },
        };

        var analysis = downloader.BuildAnalysis(data);

        Assert.HasCount(2, analysis.Children);
        Assert.AreEqual("One", analysis.Children[0].Title);
        Assert.AreEqual("https://example.com/2", analysis.Children[1].Url);
    }

    [TestMethod]
    public void BuildAnalysis_Playlist_SkipsDeadEntries()
    {
        var downloader = CreateDownloader();
        var data = new VideoData
        {
            Title = "Collection",
            Entries = new[]
            {
                new VideoData { Title = "Live", Url = "https://example.com/1" },
                // Dead/unavailable videos (terminated accounts) still appear in
                // playlist output with a URL but no title; they must not become
                // child tasks.
                new VideoData { Url = "https://example.com/dead" },
                new VideoData { Title = "Also dead", Url = "" },
            },
        };

        var analysis = downloader.BuildAnalysis(data);

        Assert.HasCount(1, analysis.Children);
        Assert.AreEqual("Live", analysis.Children[0].Title);
    }

    [TestMethod]
    public async Task AnalyzeAsync_UsesFetchedData()
    {
        var downloader = CreateDownloader(async (_, _, _) =>
            new RunResult<VideoData>(
                true,
                Array.Empty<string>(),
                new VideoData { Title = "Fetched", Extension = "webm", Url = "https://example.com/v.webm" }));

        var analysis = await downloader.AnalyzeAsync("https://www.example.com/video/xyz789", "task-1", CancellationToken.None);

        Assert.AreEqual("Fetched", analysis.Title);
        Assert.AreEqual("Fetched.webm", analysis.SuggestedFileName);
    }

    [TestMethod]
    public async Task AnalyzeAsync_FetchFailure_Throws()
    {
        var downloader = CreateDownloader(async (_, _, _) =>
            new RunResult<VideoData>(false, new[] { "ERROR: Unsupported URL" }, null!));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => downloader.AnalyzeAsync("https://example.com/page", "task-1", CancellationToken.None));
    }

    [TestMethod]
    public async Task AnalyzeAsync_AuthError_GenericDownloaderDoesNotRetry()
    {
        // The generic YtdlpDownloader does not enable the cookie attach-and-
        // retry (only YouTube does), so an auth-class error surfaces as a
        // plain InvalidOperationException without a retry.
        var calls = 0;
        var downloader = CreateDownloader(async (_, _, _) =>
        {
            calls++;
            return new RunResult<VideoData>(
                false,
                new[] { "ERROR: [site] abc: Sign in to confirm you're not a bot. Use --cookies." },
                null!);
        });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => downloader.AnalyzeAsync("https://www.example.com/video/xyz789", "task-1", CancellationToken.None));

        Assert.AreEqual(1, calls); // no cookie retry on the generic downloader
    }

    [TestMethod]
    public async Task AnalyzeAsync_TimeoutError_ThrowsRetryable()
    {
        // A transient failure (timeout) is wrapped in RetryableException so
        // the task service can retry it.
        var downloader = CreateDownloader(async (_, _, _) =>
            new RunResult<VideoData>(false, new[] { "ERROR: [site] abc: Connection timed out" }, null!));

        await Assert.ThrowsExactlyAsync<RetryableException>(
            () => downloader.AnalyzeAsync("https://www.example.com/video/xyz789", "task-1", CancellationToken.None));
    }

    [TestMethod]
    public async Task AnalyzeAsync_NotFoundError_ThrowsInvalidOperation()
    {
        // A deterministic error (resource gone) is not retryable.
        var downloader = CreateDownloader(async (_, _, _) =>
            new RunResult<VideoData>(false, new[] { "ERROR: [site] abc: Video unavailable" }, null!));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => downloader.AnalyzeAsync("https://www.example.com/video/xyz789", "task-1", CancellationToken.None));
    }

    private sealed class DirectProxyService : IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public bool ShouldProxyFast(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;

        public string? GetBrowserProxyAddress() => null;
    }
}
