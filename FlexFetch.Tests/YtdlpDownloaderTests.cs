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
        var config = new InMemoryConfigRepository();
        var proxy = new DirectProxyService();
        var ytdlp = new YtdlpService(proxy, config, Log, dir);
        var storage = new StorageService(dir);
        var store = new LiteDbStore(Path.Combine(dir, "flexfetch.db"));
        var cookies = new CookiePoolService(new CookieRepository(store));
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
            new YtdlpService(new DirectProxyService(), new InMemoryConfigRepository(), Log, ytDir),
            new DirectProxyService(),
            new StorageService(ytDir),
            Log,
            new CookiePoolService(new CookieRepository(ytStore)));

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

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;
    }
}
