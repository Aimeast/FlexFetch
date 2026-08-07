using FlexFetch.Services;

namespace FlexFetch.Tests;

[TestClass]
public sealed class FileMimeTests
{
    [TestMethod]
    public void For_VideoExtensions_ReturnsVideoMime()
    {
        Assert.AreEqual("video/mp4", FileMime.For("clip.mp4"));
        Assert.AreEqual("video/webm", FileMime.For("clip.webm"));
        Assert.AreEqual("video/mp4", FileMime.For("CLIP.M4V"));
        Assert.AreEqual("video/quicktime", FileMime.For("movie.mov"));
        Assert.AreEqual("video/x-matroska", FileMime.For("movie.mkv"));
        Assert.AreEqual("video/mp2t", FileMime.For("stream.ts"));
    }

    [TestMethod]
    public void For_AudioExtensions_ReturnsAudioMime()
    {
        Assert.AreEqual("audio/mpeg", FileMime.For("song.mp3"));
        Assert.AreEqual("audio/flac", FileMime.For("song.flac"));
        Assert.AreEqual("audio/ogg", FileMime.For("song.ogg"));
        Assert.AreEqual("audio/mp4", FileMime.For("song.m4a"));
        Assert.AreEqual("audio/wav", FileMime.For("song.wav"));
    }

    [TestMethod]
    public void For_NonMediaExtensions_ReturnsOctetStream()
    {
        // Only audio/video are mapped; everything else downloads as octet-stream.
        Assert.AreEqual("application/octet-stream", FileMime.For("pic.png"));
        Assert.AreEqual("application/octet-stream", FileMime.For("doc.pdf"));
        Assert.AreEqual("application/octet-stream", FileMime.For("data.bin"));
        Assert.AreEqual("application/octet-stream", FileMime.For("noextension"));
        Assert.AreEqual("application/octet-stream", FileMime.For(null));
    }
}
