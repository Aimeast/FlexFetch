using FlexFetch.Services.Downloaders;

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

}
