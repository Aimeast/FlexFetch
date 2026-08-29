using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Tasks;
using Serilog;

namespace FlexFetch.Tests;

[TestClass]
public sealed class DownloaderTaskExecutorTests
{
    private static readonly ILogger Log = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();

    [TestMethod]
    public async Task ExecuteAsync_ManifestContentType_KeepsYtdlpFirst()
    {
        // An extension-less URL serving an HLS manifest must be parsed by
        // yt-dlp (HLS stream or playlist expansion), not saved as a raw
        // manifest file by the generic downloader.
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            Array.Empty<byte>(),
            new Dictionary<string, string> { ["Content-Type"] = "application/vnd.apple.mpegurl" }));

        var task = new TaskItem { OwnerUserId = "user-1", Url = server.BaseUrl + "/playlist" };
        var executor = CreateExecutor();

        await executor.ExecuteAsync(task, _ => { }, CancellationToken.None);

        Assert.AreEqual("Ytdlp", task.DownloaderType);
    }

    [TestMethod]
    public async Task ExecuteAsync_MediaContentType_PromotesGenericFirst()
    {
        // A plain direct media link (extension-less, video Content-Type) is
        // still handed to the generic file downloader right away.
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            Array.Empty<byte>(),
            new Dictionary<string, string> { ["Content-Type"] = "video/mp4" }));

        var task = new TaskItem { OwnerUserId = "user-1", Url = server.BaseUrl + "/clip" };
        var executor = CreateExecutor();

        await executor.ExecuteAsync(task, _ => { }, CancellationToken.None);

        Assert.AreEqual("Generic", task.DownloaderType);
    }

    private static DownloaderTaskExecutor CreateExecutor()
    {
        var factory = new DownloaderFactory(new IDownloader[]
        {
            new FakeDownloader("Generic"),
            new FakeDownloader("Ytdlp"),
        });
        return new DownloaderTaskExecutor(factory, new DirectProxyService(), Log);
    }

    private sealed class DirectProxyService : IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public bool ShouldProxyFast(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;

        public string? GetBrowserProxyAddress() => null;
    }

    private sealed class FakeDownloader : IDownloader
    {
        public FakeDownloader(string type) => Type = type;

        public string Type { get; }

        public bool IsDomainSpecific => false;

        public bool CanHandle(string url) =>
            url.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        public Task<AnalysisResult> AnalyzeAsync(string url, string taskId, CancellationToken ct) =>
            Task.FromResult(new AnalysisResult { Title = "t", DirectUrl = url });

        public Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
