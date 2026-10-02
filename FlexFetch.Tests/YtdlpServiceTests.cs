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
}
