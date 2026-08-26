using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Refresh;
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
    public void SelectDownloaders_SpecificDownloader_ReturnsItAlone()
    {
        var youtube = new FakeDownloader("YouTube", isDomainSpecific: true, "youtube.com");
        var generic = new FakeDownloader("Generic", isDomainSpecific: false, "http");
        var factory = new DownloaderFactory(new IDownloader[] { generic, youtube });

        var selected = factory.SelectDownloaders("https://www.youtube.com/watch?v=abc");

        // A domain-specific downloader is used alone - no generic fallback.
        Assert.HasCount(1, selected);
        Assert.AreEqual("YouTube", selected[0].Type);
    }

    [TestMethod]
    public void SelectDownloaders_MultipleSpecificMatches_Throws()
    {
        var a = new FakeDownloader("A", isDomainSpecific: true, "youtube.com");
        var b = new FakeDownloader("B", isDomainSpecific: true, "youtube.com");
        var factory = new DownloaderFactory(new IDownloader[] { a, b });

        Assert.ThrowsExactly<InvalidOperationException>(
            () => factory.SelectDownloaders("https://www.youtube.com/watch?v=abc"));
    }

    [TestMethod]
    public void SelectDownloaders_NoSpecificMatch_UsesGenericChain()
    {
        var youtube = new FakeDownloader("YouTube", isDomainSpecific: true, "youtube.com");
        var generic = new FakeDownloader("Generic", isDomainSpecific: false, "http");
        var factory = new DownloaderFactory(new IDownloader[] { youtube, generic });

        var selected = factory.SelectDownloaders("https://example.com/file.bin");

        Assert.HasCount(1, selected);
        Assert.AreEqual("Generic", selected[0].Type);
    }

    [TestMethod]
    public void SelectDownloaders_DirectMediaLink_PromotesGenericFirst()
    {
        var ytdlp = new FakeDownloader("Ytdlp", isDomainSpecific: false, "http");
        var generic = new FakeDownloader("Generic", isDomainSpecific: false, "http");
        var factory = new DownloaderFactory(new IDownloader[] { ytdlp, generic });

        // A direct media URL (no specific match): generic goes first.
        var selected = factory.SelectDownloaders("https://cdn.example.com/clip.mp4");

        Assert.HasCount(2, selected);
        Assert.AreEqual("Generic", selected[0].Type);
        Assert.AreEqual("Ytdlp", selected[1].Type);
    }

    [TestMethod]
    public void SelectDownloaders_PageUrl_KeepsGenericChainOrder()
    {
        var generic = new FakeDownloader("Generic", isDomainSpecific: false, "http");
        var ytdlp = new FakeDownloader("Ytdlp", isDomainSpecific: false, "http");
        var factory = new DownloaderFactory(new IDownloader[] { generic, ytdlp });

        // Generic chain order is fixed (Ytdlp first) regardless of
        // registration order.
        var selected = factory.SelectDownloaders("https://example.com/watch?v=123");

        Assert.AreEqual("Ytdlp", selected[0].Type);
        Assert.AreEqual("Generic", selected[1].Type);
    }

    [TestMethod]
    public void Fallback_IsGenericDownloader()
    {
        var high = new FakeDownloader("High", isDomainSpecific: true, "youtube.com");
        var generic = new FakeDownloader("Generic", isDomainSpecific: false, "http");
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
            services.AddSingleton(sp => new CookiePoolService(sp.GetRequiredService<ICookieRepository>(), new ICookieDomainMapping[] { new YouTubeCookieDomainMapping(), new DefaultCookieDomainMapping() }));
            services.AddSingleton(sp => new YtdlpService(
                sp.GetRequiredService<IProxyService>(),
                sp.GetRequiredService<IConfigRepository>(),
                sp.GetRequiredService<Serilog.ILogger>(),
                dir));
            services.AddSingleton<ICookieRefreshStrategy, DefaultCookieRefreshStrategy>();
            services.AddSingleton<ICookieRefreshStrategy, YouTubeCookieRefreshStrategy>();
            services.AddSingleton(sp => new StealthBrowserService(
                sp.GetRequiredService<IProxyService>(),
                new CookiePoolService(sp.GetRequiredService<ICookieRepository>(), new ICookieDomainMapping[] { new YouTubeCookieDomainMapping(), new DefaultCookieDomainMapping() }),
                sp.GetRequiredService<StorageService>(),
                sp.GetRequiredService<IConfigRepository>(),
                sp.GetRequiredService<Serilog.ILogger>(),
                sp.GetServices<ICookieRefreshStrategy>()));
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

            // Generic is the fallback at the end of the chain.
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

        public bool ShouldProxyFast(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;

        public string? GetBrowserProxyAddress() => null;
    }

    private sealed class FakeDownloader : IDownloader
    {
        private readonly string[] _needles;

        public FakeDownloader(string type, bool isDomainSpecific, params string[] needles)
        {
            Type = type;
            IsDomainSpecific = isDomainSpecific;
            _needles = needles;
        }

        public string Type { get; }

        public bool IsDomainSpecific { get; }

        public bool CanHandle(string url) =>
            _needles.Any(n => url.Contains(n, StringComparison.OrdinalIgnoreCase));

        public Task<AnalysisResult> AnalyzeAsync(string url, string taskId, CancellationToken ct) =>
            Task.FromResult(new AnalysisResult { Title = "t", DirectUrl = url });

        public Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
