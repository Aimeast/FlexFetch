using FlexFetch.Services.Downloaders;

namespace FlexFetch.Tests;

[TestClass]
public sealed class FileNameRulesTests
{
    [TestMethod]
    public void Sanitize_ReplacesIllegalCharacters()
    {
        var name = FileNameRules.Sanitize(@"a/b\c:d*e?f""g<h>i|j.txt");

        Assert.AreEqual("a_b_c_d_e_f_g_h_i_j.txt", name);
    }

    [TestMethod]
    public void Sanitize_TrimsAndDefaults()
    {
        Assert.AreEqual("download", FileNameRules.Sanitize("   "));
        Assert.AreEqual("file.bin", FileNameRules.Sanitize("  file.bin  "));
    }

    [TestMethod]
    public void Sanitize_CapsLengthKeepingExtension()
    {
        var longName = new string('a', 300) + ".mp4";

        var result = FileNameRules.Sanitize(longName);

        Assert.IsLessThanOrEqualTo(180, System.Text.Encoding.UTF8.GetByteCount(result));
        StringAssert.EndsWith(result, ".mp4");
    }

    [TestMethod]
    public void Sanitize_HandlesMultibyteUtf8()
    {
        // Each CJK char is 3 bytes in UTF-8; 60 chars = 180 bytes exactly.
        var name = new string('中', 60) + ".mp4";

        var result = FileNameRules.Sanitize(name);

        Assert.IsLessThanOrEqualTo(180, System.Text.Encoding.UTF8.GetByteCount(result));
        StringAssert.EndsWith(result, ".mp4");
    }

    [TestMethod]
    public void EnsureUnique_KeepsFirstAndSuffixesCollisions()
    {
        var existing = new[] { "video.mp4", "song.mp3" };

        Assert.AreEqual("photo.mp4", FileNameRules.EnsureUnique("photo.mp4", existing));
        Assert.AreEqual("video (2).mp4", FileNameRules.EnsureUnique("video.mp4", existing));
        Assert.AreEqual("video (3).mp4", FileNameRules.EnsureUnique("video.mp4", existing.Concat(new[] { "video (2).mp4" })));
    }

    [TestMethod]
    public void EnsureUnique_IsCaseInsensitive()
    {
        Assert.AreEqual("Video (2).mp4", FileNameRules.EnsureUnique("Video.mp4", new[] { "video.mp4" }));
    }

    [TestMethod]
    public void InferFromUrl_UsesLastPathSegment()
    {
        Assert.AreEqual("file.bin", FileNameRules.InferFromUrl("https://example.com/path/file.bin"));
        Assert.AreEqual("file.bin", FileNameRules.InferFromUrl("https://example.com/path/file.bin?token=1"));
        Assert.AreEqual("example.com", FileNameRules.InferFromUrl("https://example.com/"));
        Assert.AreEqual("download", FileNameRules.InferFromUrl("not-a-url"));
    }
}
