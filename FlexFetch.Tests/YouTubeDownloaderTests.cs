using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using FlexFetch.Services;
using FlexFetch.Config;
using FlexFetch.Data;
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
        return new YouTubeDownloader(ytdlp, proxy, storage, Log, fetch);
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
            () => downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", CancellationToken.None));
    }

    [TestMethod]
    public async Task AnalyzeAsync_UsesFetchedData()
    {
        var downloader = CreateDownloader(async (_, _, _) =>
            new RunResult<VideoData>(
                true,
                Array.Empty<string>(),
                new VideoData { Title = "Fetched Title", Extension = "webm", Url = "https://example.com/v.webm" }));

        var analysis = await downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", CancellationToken.None);

        Assert.AreEqual("Fetched Title", analysis.Title);
        Assert.AreEqual("Fetched Title.webm", analysis.SuggestedFileName);
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
