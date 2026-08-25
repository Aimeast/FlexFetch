using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
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
public sealed class YouTubeDownloaderTests
{
    private static readonly ILogger Log = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();

    private static YouTubeDownloader CreateDownloader(Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>>? fetch = null)
    {
        var dir = TestApp.CreateTempDataDir();
        var config = new InMemoryConfigRepository();
        var proxy = new DirectProxyService();
        var ytdlp = new YtdlpService(proxy, config, Log, dir);
        var storage = new StorageService(dir);
        var store = new LiteDbStore(Path.Combine(dir, "flexfetch.db"));
        var cookies = new CookiePoolService(new CookieRepository(store));
        return new YouTubeDownloader(ytdlp, proxy, storage, Log, cookies, fetch);
    }

    private static (YouTubeDownloader Downloader, CookiePoolService Pool) CreateDownloaderWithPool(
        Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>>? fetch = null,
        Func<string, OptionSet, Action<double>, CancellationToken, Task<RunResult<string>>>? download = null)
    {
        var dir = TestApp.CreateTempDataDir();
        var config = new InMemoryConfigRepository();
        var proxy = new DirectProxyService();
        var ytdlp = new YtdlpService(proxy, config, Log, dir);
        var storage = new StorageService(dir);
        var store = new LiteDbStore(Path.Combine(dir, "flexfetch.db"));
        var pool = new CookiePoolService(new CookieRepository(store));
        return (new YouTubeDownloader(ytdlp, proxy, storage, Log, pool, fetch, download), pool);
    }

    [TestMethod]
    public void CanHandle_MatchesYouTubeDomains()
    {
        var downloader = CreateDownloader();

        Assert.IsTrue(downloader.CanHandle("https://www.youtube.com/watch?v=abc"));
        Assert.IsTrue(downloader.CanHandle("https://youtu.be/abc"));
        Assert.IsFalse(downloader.CanHandle("https://x.com/SpaceX/status/1"));
        Assert.IsFalse(downloader.CanHandle("https://example.com/video.mp4"));
    }

    [TestMethod]
    public void BuildOptions_SetsFormatAndMerge()
    {
        var downloader = CreateDownloader();

        var options = downloader.BuildOptions(new Uri("https://www.youtube.com/watch?v=abc"));

        Assert.AreEqual("bestvideo+bestaudio/best", options.Format);
        Assert.AreEqual(DownloadMergeFormat.Mp4, options.MergeOutputFormat);
    }

    [TestMethod]
    public void BuildOptions_IncludesProxyWhenConfigured()
    {
        var downloader = CreateDownloader();
        // No proxy in this test setup -> Proxy must be null.
        var options = downloader.BuildOptions(new Uri("https://www.youtube.com/watch?v=abc"));

        Assert.IsNull(options.Proxy);
    }

    [TestMethod]
    public void BuildOptions_SetsResilientPlayerClients()
    {
        var downloader = CreateDownloader();

        var options = downloader.BuildOptions(new Uri("https://www.youtube.com/watch?v=abc"));

        // A bot-challenge ("The page needs to be reloaded") is common on
        // datacenter IPs. web_safari additionally demands a po_token and
        // fails without one, so only clients that do not require it are used;
        // the cookie attach-and-retry flow is unchanged.
        Assert.AreEqual("youtube:player_client=android,tv,mweb", (string)options.ExtractorArgs);
    }

    [TestMethod]
    public void BuildAnalysis_SingleVideo_SuggestsTitleFileName()
    {
        var downloader = CreateDownloader();
        var data = new VideoData
        {
            Title = "My Video Title",
            Extension = "mp4",
            Url = "https://example.com/v.mp4",
        };

        var analysis = downloader.BuildAnalysis(data);

        Assert.AreEqual("My Video Title", analysis.Title);
        Assert.IsEmpty(analysis.Children);
        Assert.AreEqual("My Video Title.mp4", analysis.SuggestedFileName);
    }

    [TestMethod]
    public void BuildAnalysis_Playlist_ExpandsChildren()
    {
        var downloader = CreateDownloader();
        var data = new VideoData
        {
            Title = "Playlist",
            Entries = new[]
            {
                new VideoData { Title = "Video 1", Url = "https://example.com/1" },
                new VideoData { Title = "Video 2", Url = "https://example.com/2" },
                new VideoData { Title = "Skipped" }, // no URL -> dropped
            },
        };

        var analysis = downloader.BuildAnalysis(data);

        Assert.HasCount(2, analysis.Children);
        Assert.AreEqual("Video 1", analysis.Children[0].Title);
        Assert.AreEqual("https://example.com/2", analysis.Children[1].Url);
    }

    [TestMethod]
    public async Task AnalyzeAsync_FetchFailure_Throws()
    {
        var downloader = CreateDownloader(async (_, _, _) =>
            new RunResult<VideoData>(false, new[] { "ERROR: Unsupported URL" }, null!));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None));
    }

    [TestMethod]
    public async Task AnalyzeAsync_UsesFetchedData()
    {
        var downloader = CreateDownloader(async (_, _, _) =>
            new RunResult<VideoData>(
                true,
                Array.Empty<string>(),
                new VideoData { Title = "Fetched Title", Extension = "webm", Url = "https://example.com/v.webm" }));

        var analysis = await downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None);

        Assert.AreEqual("Fetched Title", analysis.Title);
        Assert.AreEqual("Fetched Title.webm", analysis.SuggestedFileName);
    }

    [TestMethod]
    public async Task AnalyzeAsync_AuthError_NoCookies_ThrowsAuthRequired()
    {
        var (downloader, _) = CreateDownloaderWithPool(async (_, _, _) =>
            new RunResult<VideoData>(
                false,
                new[] { "ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies." },
                null!));

        var ex = await Assert.ThrowsExactlyAsync<AuthRequiredException>(
            () => downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None));

        Assert.AreEqual(AuthFailureReason.LoginRequired, ex.Reason);
    }

    [TestMethod]
    public async Task AnalyzeAsync_AuthError_WithCookies_RetriesOnceThenSucceeds()
    {
        var calls = 0;
        string? cookieFileSeen = null;
        var existsDuringRetry = false;
        var (downloader, pool) = CreateDownloaderWithPool(async (url, options, ct) =>
        {
            calls++;
            if (calls == 1)
            {
                return new RunResult<VideoData>(
                    false,
                    new[] { "ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies." },
                    null!);
            }

            cookieFileSeen = options.Cookies;
            existsDuringRetry = File.Exists(options.Cookies);
            return new RunResult<VideoData>(
                true,
                Array.Empty<string>(),
                new VideoData { Title = "Fetched Title", Extension = "webm", Url = "https://example.com/v.webm" });
        });
        pool.UpsertCookies(new[] { new CookieItem { Domain = ".youtube.com", Name = "SID", Value = "x" } });

        var analysis = await downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None);

        // Retried exactly once, and the retry carried the cookie file.
        Assert.AreEqual(2, calls);
        Assert.IsNotNull(cookieFileSeen);
        Assert.IsTrue(existsDuringRetry, "cookie file should exist during the retry");
        Assert.AreEqual("Fetched Title", analysis.Title);

        // The temporary cookie file is cleaned up after the retry.
        Assert.IsFalse(File.Exists(cookieFileSeen), "cookie file should be deleted after the retry");
    }

    [TestMethod]
    public async Task AnalyzeAsync_AuthError_RetryStillFails_ThrowsAuthRequired()
    {
        var calls = 0;
        var (downloader, pool) = CreateDownloaderWithPool(async (_, _, _) =>
        {
            calls++;
            return new RunResult<VideoData>(
                false,
                new[] { "ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies." },
                null!);
        });
        pool.UpsertCookies(new[] { new CookieItem { Domain = ".youtube.com", Name = "SID", Value = "x" } });

        var ex = await Assert.ThrowsExactlyAsync<AuthRequiredException>(
            () => downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None));

        Assert.AreEqual(2, calls); // initial + cookie retry
        Assert.AreEqual(AuthFailureReason.LoginRequired, ex.Reason);
    }

    [TestMethod]
    public async Task AnalyzeAsync_AuthError_NonYoutubeDomain_NoCookiesMatch()
    {
        // Cookies for .youtube.com do not match an unrelated host; the auth
        // error still maps to LoginRequired and no retry is possible.
        var calls = 0;
        var (downloader, pool) = CreateDownloaderWithPool(async (_, _, _) =>
        {
            calls++;
            return new RunResult<VideoData>(
                false,
                new[] { "ERROR: [youtube] abc: private video." },
                null!);
        });
        pool.UpsertCookies(new[] { new CookieItem { Domain = ".example.com", Name = "SID", Value = "x" } });

        var ex = await Assert.ThrowsExactlyAsync<AuthRequiredException>(
            () => downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None));

        Assert.AreEqual(AuthFailureReason.Private, ex.Reason);
        Assert.AreEqual(1, calls); // no matching cookie -> no retry
    }

    [TestMethod]
    public async Task DownloadAsync_WithPoolCookies_AttachesCookieFile()
    {
        string? cookieFileSeen = null;
        var existsDuringDownload = false;
        var (downloader, pool) = CreateDownloaderWithPool(
            download: async (_, options, _, _) =>
            {
                cookieFileSeen = options.Cookies;
                existsDuringDownload = cookieFileSeen is not null && File.Exists(cookieFileSeen);
                return new RunResult<string>(true, Array.Empty<string>(), "video.mp4");
            });
        pool.UpsertCookies(new[] { new CookieItem { Domain = ".youtube.com", Name = "SID", Value = "x" } });

        var task = new TaskItem { Id = "task-dl-1", Url = "https://www.youtube.com/watch?v=abc" };
        var analysis = new AnalysisResult { Title = "T", SuggestedFileName = "T.mp4" };

        await downloader.DownloadAsync(task, analysis, _ => { }, CancellationToken.None);

        // The download carried the pool cookies and the file was cleaned up.
        Assert.IsNotNull(cookieFileSeen);
        Assert.IsTrue(existsDuringDownload, "cookie file should exist during the download");
        Assert.IsFalse(File.Exists(cookieFileSeen), "cookie file should be deleted after the download");
    }

    [TestMethod]
    public async Task DownloadAsync_AuthError_NoCookies_ThrowsAuthRequired()
    {
        var calls = 0;
        var (downloader, _) = CreateDownloaderWithPool(
            download: async (_, _, _, _) =>
            {
                calls++;
                return new RunResult<string>(
                    false,
                    new[] { "ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies." },
                    null!);
            });

        var task = new TaskItem { Id = "task-dl-2", Url = "https://www.youtube.com/watch?v=abc" };
        var analysis = new AnalysisResult { Title = "T", SuggestedFileName = "T.mp4" };

        var ex = await Assert.ThrowsExactlyAsync<AuthRequiredException>(
            () => downloader.DownloadAsync(task, analysis, _ => { }, CancellationToken.None));

        Assert.AreEqual(AuthFailureReason.LoginRequired, ex.Reason);
        Assert.AreEqual(1, calls); // no matching cookie -> no retry
    }

    [TestMethod]
    public async Task DownloadAsync_AuthError_RetriesWithCookiesThenSucceeds()
    {
        var calls = 0;
        string? cookieFileSeen = null;
        CookiePoolService? poolRef = null;
        var (downloader, pool) = CreateDownloaderWithPool(
            download: async (_, options, _, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    // The first attempt has no pool cookies yet; simulate the
                    // cookies appearing before the retry (e.g. manual import).
                    poolRef!.UpsertCookies(new[] { new CookieItem { Domain = ".youtube.com", Name = "SID", Value = "x" } });
                    return new RunResult<string>(
                        false,
                        new[] { "ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies." },
                        null!);
                }

                cookieFileSeen = options.Cookies;
                return new RunResult<string>(true, Array.Empty<string>(), "video.mp4");
            });
        poolRef = pool;

        var task = new TaskItem { Id = "task-dl-3", Url = "https://www.youtube.com/watch?v=abc" };
        var analysis = new AnalysisResult { Title = "T", SuggestedFileName = "T.mp4" };

        await downloader.DownloadAsync(task, analysis, _ => { }, CancellationToken.None);

        // Retried exactly once, and the retry carried the cookie file.
        Assert.AreEqual(2, calls);
        Assert.IsNotNull(cookieFileSeen);
        Assert.IsFalse(File.Exists(cookieFileSeen), "cookie file should be deleted after the retry");
    }

    [TestMethod]
    public async Task DownloadAsync_AuthError_RetryStillFails_ThrowsAuthRequired()
    {
        var calls = 0;
        var (downloader, pool) = CreateDownloaderWithPool(
            download: async (_, _, _, _) =>
            {
                calls++;
                return new RunResult<string>(
                    false,
                    new[] { "ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies." },
                    null!);
            });
        pool.UpsertCookies(new[] { new CookieItem { Domain = ".youtube.com", Name = "SID", Value = "x" } });

        var task = new TaskItem { Id = "task-dl-4", Url = "https://www.youtube.com/watch?v=abc" };
        var analysis = new AnalysisResult { Title = "T", SuggestedFileName = "T.mp4" };

        var ex = await Assert.ThrowsExactlyAsync<AuthRequiredException>(
            () => downloader.DownloadAsync(task, analysis, _ => { }, CancellationToken.None));

        Assert.AreEqual(AuthFailureReason.LoginRequired, ex.Reason);
    }

    private sealed class InMemoryConfigRepository : IConfigRepository
    {
        private readonly Dictionary<string, string> _values = new();

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public IReadOnlyDictionary<string, string> GetAll() => _values;

        public void Set(string key, string value) => _values[key] = value;

        public bool Delete(string key) => _values.Remove(key);
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
