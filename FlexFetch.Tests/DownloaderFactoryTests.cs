using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace FlexFetch.Tests;

[TestClass]
public sealed class DownloaderFactoryTests
{
    private static readonly Serilog.ILogger Log = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();

    [TestMethod]
    public void SelectDownloaders_ReturnsMatching_OrderedByPriority()
    {
        var high = new FakeDownloader("High", 100, "youtube.com");
        var low = new FakeDownloader("Low", 10, "youtube.com");
        var generic = new FakeDownloader("Generic", 0, "http");
        var factory = new DownloaderFactory(new IDownloader[] { generic, low, high });

        var selected = factory.SelectDownloaders("https://www.youtube.com/watch?v=abc");

        Assert.HasCount(3, selected);
        Assert.AreEqual("High", selected[0].Type);
        Assert.AreEqual("Low", selected[1].Type);
        Assert.AreEqual("Generic", selected[2].Type);
    }

    [TestMethod]
    public void SelectDownloaders_FiltersNonMatching()
    {
        var youtube = new FakeDownloader("YouTube", 100, "youtube.com");
        var generic = new FakeDownloader("Generic", 0, "http");
        var factory = new DownloaderFactory(new IDownloader[] { youtube, generic });

        var selected = factory.SelectDownloaders("https://example.com/file.bin");

        Assert.HasCount(1, selected);
        Assert.AreEqual("Generic", selected[0].Type);
    }

    [TestMethod]
    public void SelectDownloaders_DirectMediaLink_PromotesGenericFirst()
    {
        var ytdlp = new FakeDownloader("Ytdlp", 80, "http");
        var generic = new FakeDownloader("Generic", 0, "http");
        var factory = new DownloaderFactory(new IDownloader[] { ytdlp, generic });

        // A direct media URL: even though Ytdlp has higher priority and matches,
        // the generic downloader must go first for a direct file.
        var selected = factory.SelectDownloaders("https://cdn.example.com/clip.mp4");

        Assert.HasCount(2, selected);
        Assert.AreEqual("Generic", selected[0].Type);
        Assert.AreEqual("Ytdlp", selected[1].Type);
    }

    [TestMethod]
    public void SelectDownloaders_PageUrl_KeepsPriorityOrder()
    {
        var ytdlp = new FakeDownloader("Ytdlp", 80, "http");
        var generic = new FakeDownloader("Generic", 0, "http");
        var factory = new DownloaderFactory(new IDownloader[] { ytdlp, generic });

        // A page URL (no media extension) keeps the priority order: Ytdlp first.
        var selected = factory.SelectDownloaders("https://example.com/watch?v=123");

        Assert.AreEqual("Ytdlp", selected[0].Type);
        Assert.AreEqual("Generic", selected[1].Type);
    }

    [TestMethod]
    public void Fallback_IsLowestPriorityDownloader()
    {
        var high = new FakeDownloader("High", 100, "youtube.com");
        var generic = new FakeDownloader("Generic", 0, "http");
        var factory = new DownloaderFactory(new IDownloader[] { high, generic });

        Assert.AreEqual("Generic", factory.Fallback!.Type);
    }

    [TestMethod]
    public void Create_DiscoversAllDownloadersViaReflection()
    {
        var dir = TestApp.CreateTempDataDir();
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<Serilog.ILogger>(Log);
            services.AddSingleton<IProxyService>(new DirectProxyService());
            services.AddSingleton(new StorageService(dir));
            services.AddSingleton(new LiteDbStore(Path.Combine(dir, "flexfetch.db")));
            services.AddSingleton<IConfigRepository>(sp => new ConfigRepository(sp.GetRequiredService<LiteDbStore>()));
            services.AddSingleton<ICookieRepository>(sp => new CookieRepository(sp.GetRequiredService<LiteDbStore>()));
            services.AddSingleton(sp => new YtdlpService(
                sp.GetRequiredService<IProxyService>(),
                sp.GetRequiredService<IConfigRepository>(),
                sp.GetRequiredService<Serilog.ILogger>(),
                dir));
            services.AddSingleton(sp => new StealthBrowserService(
                sp.GetRequiredService<IProxyService>(),
                new CookiePoolService(sp.GetRequiredService<ICookieRepository>()),
                sp.GetRequiredService<StorageService>(),
                sp.GetRequiredService<IConfigRepository>(),
                sp.GetRequiredService<Serilog.ILogger>()));
            var provider = services.BuildServiceProvider();

            var factory = DownloaderFactory.Create(provider);

            // All concrete IDownloader implementations are discovered:
            // GenericFileDownloader, YtdlpDownloader, HtmlResourceDetector,
            // BrowserParsingDownloader, TwitterDownloader, YouTubeDownloader.
            var types = factory.All.Select(d => d.Type).ToList();
            Assert.Contains("Generic", types);
            Assert.Contains("Ytdlp", types);
            Assert.Contains("YouTube", types);
            Assert.Contains("Twitter", types);
            Assert.Contains("Html", types);
            Assert.Contains("Browser", types);

            // Ordered by priority: YouTube(100) first, Generic(0) fallback last.
            Assert.AreEqual("YouTube", factory.All[0].Type);
            Assert.AreEqual("Generic", factory.Fallback!.Type);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class DirectProxyService : IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;
    }

    private sealed class FakeDownloader : IDownloader
    {
        private readonly string[] _needles;

        public FakeDownloader(string type, int priority, params string[] needles)
        {
            Type = type;
            Priority = priority;
            _needles = needles;
        }

        public string Type { get; }

        public int Priority { get; }

        public bool CanHandle(string url) =>
            _needles.Any(n => url.Contains(n, StringComparison.OrdinalIgnoreCase));

        public Task<AnalysisResult> AnalyzeAsync(string url, CancellationToken ct) =>
            Task.FromResult(new AnalysisResult { Title = "t", DirectUrl = url });

        public Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
