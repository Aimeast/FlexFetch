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
        Assert.IsTrue(DirectLinkDetector.HasMediaExtension("https://cdn.example.com/list.m3u"));
        Assert.IsTrue(DirectLinkDetector.HasMediaExtension("https://cdn.example.com/audio.mp3"));
        Assert.IsTrue(DirectLinkDetector.HasMediaExtension("https://cdn.example.com/file.zip"));
        Assert.IsFalse(DirectLinkDetector.HasMediaExtension("https://example.com/watch?v=123"));
        Assert.IsFalse(DirectLinkDetector.HasMediaExtension("https://example.com/page.html"));
    }

    [TestMethod]
    public void IsManifestExtension_MatchesPlaylistManifestsOnly()
    {
        Assert.IsTrue(DirectLinkDetector.IsManifestExtension("https://cdn.example.com/stream.m3u8?token=1"));
        Assert.IsTrue(DirectLinkDetector.IsManifestExtension("https://cdn.example.com/list.m3u"));
        Assert.IsTrue(DirectLinkDetector.IsManifestExtension("https://cdn.example.com/manifest.mpd"));
        Assert.IsFalse(DirectLinkDetector.IsManifestExtension("https://cdn.example.com/clip.mp4"));
        Assert.IsFalse(DirectLinkDetector.IsManifestExtension("https://example.com/watch?v=123"));
        Assert.IsFalse(DirectLinkDetector.IsManifestExtension("not-a-url"));
    }

    [TestMethod]
    public void IsManifestContentType_ClassifiesManifestMimeTypes()
    {
        Assert.IsTrue(DirectLinkDetector.IsManifestContentType("application/vnd.apple.mpegurl"));
        Assert.IsTrue(DirectLinkDetector.IsManifestContentType("application/x-mpegURL"));
        Assert.IsTrue(DirectLinkDetector.IsManifestContentType("application/dash+xml"));
        Assert.IsFalse(DirectLinkDetector.IsManifestContentType("video/mp4"));
        Assert.IsFalse(DirectLinkDetector.IsManifestContentType("application/octet-stream"));
        Assert.IsFalse(DirectLinkDetector.IsManifestContentType(null));
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
    public async Task ProbeAsync_ReturnsTypeAndAdvertisedName()
    {
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            Array.Empty<byte>(),
            new Dictionary<string, string>
            {
                ["Content-Type"] = "video/mp4",
                ["Content-Disposition"] = "inline; filename*=utf-8''%E6%8A%A5%E5%91%8A.pdf",
            }));

        var probe = await DirectLinkDetector.ProbeAsync(
            server.BaseUrl + "/stream", new DirectProxyService(), CancellationToken.None);

        Assert.IsNotNull(probe);
        Assert.AreEqual("video/mp4", probe.ContentType);
        Assert.AreEqual("\u62a5\u544a.pdf", probe.FileName);
    }

    [TestMethod]
    public async Task ProbeAsync_FallsBackToRangeGetWhenHeadFails()
    {
        // A HEAD failure (404, rejected, timed out) must not hide a URL the
        // download itself would succeed on: the probe retries with a
        // one-byte Range GET and names the link from those headers.
        using var server = new TestHttpServer(req =>
        {
            if (req.Method == "HEAD")
            {
                return new TestHttpServer.HttpResponse(404, Array.Empty<byte>());
            }

            return new TestHttpServer.HttpResponse(
                200,
                new byte[] { 1 },
                new Dictionary<string, string> { ["Content-Type"] = "video/mp4" });
        });

        var probe = await DirectLinkDetector.ProbeAsync(
            server.BaseUrl + "/file", new DirectProxyService(), CancellationToken.None);

        Assert.IsNotNull(probe);
        Assert.AreEqual("video/mp4", probe.ContentType);
        Assert.AreEqual(2, server.Requests.Count);
        Assert.AreEqual("HEAD", server.Requests[0].Method);
        Assert.AreEqual("GET", server.Requests[1].Method);
        Assert.AreEqual("bytes=0-0", server.Requests[1].Headers.GetValueOrDefault("Range"));
    }

    [TestMethod]
    public async Task ProbeAsync_ReturnsNullWhenUnreachable()
    {
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            404, Array.Empty<byte>()));

        var probe = await DirectLinkDetector.ProbeAsync(
            server.BaseUrl + "/missing", new DirectProxyService(), CancellationToken.None);

        Assert.IsNull(probe);
    }

    private sealed class DirectProxyService : IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public bool ShouldProxyFast(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;
    }
}
