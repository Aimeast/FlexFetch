using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;

namespace FlexFetch.Tests;

[TestClass]
public sealed class DirectLinkDetectorTests
{
    [TestMethod]
    public void HasMediaExtension_MatchesMediaAndDownloadExtensions()
    {
        Assert.IsTrue(DirectLinkDetector.HasMediaExtension("https://cdn.example.com/clip.mp4"));
        Assert.IsTrue(DirectLinkDetector.HasMediaExtension("https://cdn.example.com/stream.m3u8?token=1"));
        Assert.IsTrue(DirectLinkDetector.HasMediaExtension("https://cdn.example.com/audio.mp3"));
        Assert.IsTrue(DirectLinkDetector.HasMediaExtension("https://cdn.example.com/file.zip"));
        Assert.IsFalse(DirectLinkDetector.HasMediaExtension("https://example.com/watch?v=123"));
        Assert.IsFalse(DirectLinkDetector.HasMediaExtension("https://example.com/page.html"));
    }

    [TestMethod]
    public void IsMediaContentType_ClassifiesMimeTypes()
    {
        Assert.IsTrue(DirectLinkDetector.IsMediaContentType("video/mp4"));
        Assert.IsTrue(DirectLinkDetector.IsMediaContentType("audio/mpeg"));
        Assert.IsTrue(DirectLinkDetector.IsMediaContentType("application/vnd.apple.mpegurl"));
        Assert.IsTrue(DirectLinkDetector.IsMediaContentType("application/dash+xml"));
        Assert.IsTrue(DirectLinkDetector.IsMediaContentType("application/octet-stream"));
        Assert.IsFalse(DirectLinkDetector.IsMediaContentType("text/html"));
        Assert.IsFalse(DirectLinkDetector.IsMediaContentType("application/json"));
        Assert.IsFalse(DirectLinkDetector.IsMediaContentType(null));
    }

    [TestMethod]
    public async Task ProbeContentTypeAsync_ReturnsServedType()
    {
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            Array.Empty<byte>(),
            new Dictionary<string, string> { ["Content-Type"] = "video/mp4" }));

        var contentType = await DirectLinkDetector.ProbeContentTypeAsync(
            server.BaseUrl + "/stream", new DirectProxyService(), CancellationToken.None);

        Assert.AreEqual("video/mp4", contentType);
    }

    [TestMethod]
    public async Task ProbeContentTypeAsync_ReturnsNullOnFailure()
    {
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            404, Array.Empty<byte>()));

        var contentType = await DirectLinkDetector.ProbeContentTypeAsync(
            server.BaseUrl + "/missing", new DirectProxyService(), CancellationToken.None);

        Assert.IsNull(contentType);
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
