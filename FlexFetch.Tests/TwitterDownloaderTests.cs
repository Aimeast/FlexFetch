using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class TwitterDownloaderTests
{
    private static readonly ILogger Log = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();

    private static TwitterDownloader CreateDownloader() =>
        new(new DirectProxyService(), Log);

    [TestMethod]
    public void CanHandle_MatchesXDomains()
    {
        var downloader = CreateDownloader();

        Assert.IsTrue(downloader.CanHandle("https://x.com/SpaceX/status/123"));
        Assert.IsTrue(downloader.CanHandle("https://twitter.com/SpaceX/status/123"));
        Assert.IsFalse(downloader.CanHandle("https://www.youtube.com/watch?v=abc"));
    }

    [TestMethod]
    public async Task AnalyzeAsync_ExtractsDescriptionAndMedia()
    {
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            System.Text.Encoding.UTF8.GetBytes("""
            <html><head>
              <meta property="og:description" content="SpaceX launch footage">
            </head><body>
              <video><source content="https://video.twimg.com/ext_tw_video/1.mp4" itemProp="contentUrl"></source></video>
              <video><source content="https://video.twimg.com/ext_tw_video/2.mp4" itemProp="contentUrl"></source></video>
            </body></html>
            """)));

        var downloader = CreateDownloader();
        var analysis = await downloader.AnalyzeAsync(server.BaseUrl + "/status/1", CancellationToken.None);

        Assert.AreEqual("SpaceX launch footage", analysis.Title);
        Assert.AreEqual("SpaceX launch footage", analysis.ContentText);
        Assert.AreEqual(server.BaseUrl + "/status/1", analysis.Referrer);
        Assert.HasCount(2, analysis.Children);
        CollectionAssert.Contains(analysis.Children.Select(c => c.Url).ToList(), "https://video.twimg.com/ext_tw_video/1.mp4");
    }

    [TestMethod]
    public async Task AnalyzeAsync_NoDescription_FallsBackToXPost()
    {
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            System.Text.Encoding.UTF8.GetBytes("<html><body>no meta here</body></html>")));

        var downloader = CreateDownloader();
        var analysis = await downloader.AnalyzeAsync(server.BaseUrl + "/status/2", CancellationToken.None);

        Assert.AreEqual("x-post", analysis.Title);
        Assert.IsNull(analysis.ContentText);
    }

    private sealed class DirectProxyService : IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;
    }
}
