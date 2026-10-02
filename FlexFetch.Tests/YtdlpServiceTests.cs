using FlexFetch.Services.Downloaders;

namespace FlexFetch.Tests;

[TestClass]
public sealed class YtdlpServiceTests
{
    [TestMethod]
    public void YtDlpDownloadUrl_PerPlatform()
    {
        const string base_ = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp";

        Assert.AreEqual(base_ + ".exe", YtdlpService.YtDlpDownloadUrl(isWindows: true, isMacOS: false));
        Assert.AreEqual(base_ + "_macos", YtdlpService.YtDlpDownloadUrl(isWindows: false, isMacOS: true));
        // Linux uses the standalone PyInstaller build: the bare asset is a
        // python zipapp that slim containers cannot run.
        Assert.AreEqual(base_ + "_linux", YtdlpService.YtDlpDownloadUrl(isWindows: false, isMacOS: false));
    }

    [TestMethod]
    public void FfmpegDownloadUrl_PerPlatform()
    {
        Assert.AreEqual(
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
            YtdlpService.FfmpegDownloadUrl(isWindows: true, isLinux: false, isArm64: false));
        Assert.AreEqual(
            "https://github.com/eugeneware/ffmpeg-static/releases/latest/download/ffmpeg-linux-x64",
            YtdlpService.FfmpegDownloadUrl(isWindows: false, isLinux: true, isArm64: false));
        Assert.AreEqual(
            "https://github.com/eugeneware/ffmpeg-static/releases/latest/download/ffmpeg-linux-arm64",
            YtdlpService.FfmpegDownloadUrl(isWindows: false, isLinux: true, isArm64: true));
        Assert.AreEqual(
            "https://github.com/eugeneware/ffmpeg-static/releases/latest/download/ffmpeg-macOS-x64",
            YtdlpService.FfmpegDownloadUrl(isWindows: false, isLinux: false, isArm64: false));
        Assert.AreEqual(
            "https://github.com/eugeneware/ffmpeg-static/releases/latest/download/ffmpeg-macOS-arm64",
            YtdlpService.FfmpegDownloadUrl(isWindows: false, isLinux: false, isArm64: true));
    }

    [TestMethod]
    public void AppendToPathValue_AppendsToExistingEntries()
    {
        var existing = string.Join(Path.PathSeparator, "one", "two");
        Assert.AreEqual(
            existing + Path.PathSeparator + "three",
            YtdlpService.AppendToPathValue(existing, "three"));
    }

    [TestMethod]
    public void AppendToPathValue_HandlesEmptyCurrent()
    {
        Assert.AreEqual("dir", YtdlpService.AppendToPathValue(null, "dir"));
        Assert.AreEqual("dir", YtdlpService.AppendToPathValue(string.Empty, "dir"));
    }

    [TestMethod]
    public void AppendToPathValue_IsIdempotent()
    {
        var once = YtdlpService.AppendToPathValue("one", "dir");
        Assert.AreEqual(once, YtdlpService.AppendToPathValue(once, "dir"));
    }

    [TestMethod]
    public void AppendToPathValue_DedupeIgnoresCaseOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only path comparison");
        }

        // Already-present entries are kept once, in their original form.
        Assert.AreEqual(
            "c:" + Path.DirectorySeparatorChar + "tools",
            YtdlpService.AppendToPathValue(
                "c:" + Path.DirectorySeparatorChar + "tools",
                "C:" + Path.DirectorySeparatorChar + "Tools"));
    }

    [TestMethod]
    public async Task GetVersionAsync_MissingBinary_ReturnsNull()
    {
        // The System page reports "not installed" only when the binary is
        // really absent: the version itself comes from running the binary
        // (the Linux PyInstaller build carries no PE version resource).
        var dir = TestApp.CreateTempDataDir();
        var ytdlp = new YtdlpService(new DirectProxyService(), TestLog.Instance, dir);

        Assert.IsNull(await ytdlp.GetVersionAsync());
    }
}
