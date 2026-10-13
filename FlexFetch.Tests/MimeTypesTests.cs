using FlexFetch.Services;

namespace FlexFetch.Tests;

[TestClass]
public sealed class MimeTypesTests
{
    [TestMethod]
    public void ExtensionFor_MapsKnownTypes_CaseInsensitively()
    {
        Assert.AreEqual(".mp4", MimeTypes.ExtensionFor("video/mp4"));
        Assert.AreEqual(".mp4", MimeTypes.ExtensionFor("VIDEO/MP4"));
        Assert.AreEqual(".pdf", MimeTypes.ExtensionFor("application/pdf"));
        Assert.AreEqual(".zip", MimeTypes.ExtensionFor("application/zip"));
        Assert.AreEqual(".iso", MimeTypes.ExtensionFor("application/x-iso9660-image"));
        Assert.AreEqual(".msi", MimeTypes.ExtensionFor("application/x-msi"));
        Assert.AreEqual(".dmg", MimeTypes.ExtensionFor("application/x-apple-diskimage"));
        // The source list puts mpv first for matroska; first-listed wins.
        Assert.AreEqual(".mpv", MimeTypes.ExtensionFor("video/x-matroska"));
        // Only the first (canonical) extension of a source row is stored.
        Assert.AreEqual(".bin", MimeTypes.ExtensionFor("application/octet-stream"));
    }

    [TestMethod]
    public void ExtensionFor_PageTypes_AreAbsent()
    {
        // Page answers must never promote a URL to a file download, so the
        // page types are not in the table at all.
        Assert.IsNull(MimeTypes.ExtensionFor("text/html"));
        Assert.IsNull(MimeTypes.ExtensionFor("application/xhtml+xml"));
    }

    [TestMethod]
    public void ExtensionFor_UnmappedOrNull_ReturnsNull()
    {
        Assert.IsNull(MimeTypes.ExtensionFor("application/x-made-up"));
        Assert.IsNull(MimeTypes.ExtensionFor("application/octet-stream "));
        Assert.IsNull(MimeTypes.ExtensionFor(""));
        Assert.IsNull(MimeTypes.ExtensionFor(null));
    }

    [TestMethod]
    public void ExtensionFor_ManifestMimes_MapButRoutingChecksThemFirst()
    {
        // The table stays faithful to its source; the manifest guard in the
        // router runs before any table lookup, so these never promote a raw
        // file download.
        Assert.AreEqual(".m3u8", MimeTypes.ExtensionFor("application/x-mpegurl"));
        Assert.AreEqual(".m3u", MimeTypes.ExtensionFor("audio/mpegurl"));
    }
}
