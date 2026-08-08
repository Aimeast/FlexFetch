using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class HtmlResourceDetectorTests
{
    private static readonly ILogger Log = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();

    private static HtmlResourceDetector CreateDetector() =>
        new(new DirectProxyService(), Log);

    [TestMethod]
    public async Task AnalyzeAsync_ExtractsVideoSourcesAndTitle()
    {
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            System.Text.Encoding.UTF8.GetBytes("""
            <html><head><title>My Video Page</title></head><body>
              <video src="https://cdn.example.com/clip.mp4"></video>
              <video><source src="/media/stream.webm"></source></video>
              <meta property="og:video" content="https://cdn.example.com/og.mp4">
            </body></html>
            """)));

        var detector = CreateDetector();
        var analysis = await detector.AnalyzeAsync(server.BaseUrl + "/page", "task-1", CancellationToken.None);

        Assert.AreEqual("My Video Page", analysis.Title);
        Assert.AreEqual(server.BaseUrl + "/page", analysis.Referrer);
        Assert.HasCount(3, analysis.Children);
        CollectionAssert.Contains(analysis.Children.Select(c => c.Url).ToList(), "https://cdn.example.com/clip.mp4");
        CollectionAssert.Contains(analysis.Children.Select(c => c.Url).ToList(), server.BaseUrl + "/media/stream.webm");
        CollectionAssert.Contains(analysis.Children.Select(c => c.Url).ToList(), "https://cdn.example.com/og.mp4");
    }

    [TestMethod]
    public async Task AnalyzeAsync_NoMedia_ReturnsEmptyChildren()
    {
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            System.Text.Encoding.UTF8.GetBytes("<html><head><title>Plain Page</title></head><body>text</body></html>")));

        var detector = CreateDetector();
        var analysis = await detector.AnalyzeAsync(server.BaseUrl + "/page", "task-1", CancellationToken.None);

        Assert.IsEmpty(analysis.Children);
        Assert.AreEqual("Plain Page", analysis.Title);
    }

    private sealed class DirectProxyService : IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;
    }
}
