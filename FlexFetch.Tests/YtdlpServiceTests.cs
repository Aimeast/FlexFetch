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

    [TestMethod]
    public async Task CachedVersionAsync_ReusesCompletedProbe()
    {
        // A PyInstaller yt-dlp spawn costs seconds, so the second query must
        // reuse the first result instead of running the binary again.
        var probes = 0;
        Task<string?> Probe()
        {
            probes++;
            return Task.FromResult<string?>("2026.10.05");
        }

        Task<string?>? cached = null;
        var first = await YtdlpService.CachedVersionAsync(() => cached, v => cached = v, Probe);
        var second = await YtdlpService.CachedVersionAsync(() => cached, v => cached = v, Probe);

        Assert.AreEqual("2026.10.05", first);
        Assert.AreEqual("2026.10.05", second);
        Assert.AreEqual(1, probes);
    }

    [TestMethod]
    public async Task CachedVersionAsync_DoesNotCacheNullResult()
    {
        // Null means "missing or probe failed"; caching it would pin the
        // page to "not installed" until restart, so every call retries.
        var probes = 0;
        Task<string?> Probe()
        {
            probes++;
            return Task.FromResult<string?>(null);
        }

        Task<string?>? cached = null;
        var first = await YtdlpService.CachedVersionAsync(() => cached, v => cached = v, Probe);
        var second = await YtdlpService.CachedVersionAsync(() => cached, v => cached = v, Probe);

        Assert.IsNull(first);
        Assert.IsNull(second);
        Assert.AreEqual(2, probes);
    }

    [TestMethod]
    public async Task CachedVersionAsync_ConcurrentCallersShareOneProbe()
    {
        // Concurrent callers (page load + install log) must single-flight on
        // one probe instead of spawning duplicate processes.
        var probes = 0;
        var gate = new TaskCompletionSource();
        Task<string?> Probe()
        {
            probes++;
            return gate.Task.ContinueWith(_ => (string?)"2026.10.05");
        }

        Task<string?>? cached = null;
        var first = YtdlpService.CachedVersionAsync(() => cached, v => cached = v, Probe);
        var second = YtdlpService.CachedVersionAsync(() => cached, v => cached = v, Probe);
        gate.SetResult();

        Assert.AreEqual("2026.10.05", await first);
        Assert.AreEqual("2026.10.05", await second);
        Assert.AreEqual(1, probes);
    }

    [TestMethod]
    public async Task CachedVersionAsync_LateNullDoesNotClobberFreshResult()
    {
        // After an install resets the cache and a new probe stores the fresh
        // version, a still-running old probe completing with null must not
        // clear the stored value.
        var slow = new TaskCompletionSource();
        Task<string?>? cached = null;
        var slowCall = YtdlpService.CachedVersionAsync(
            () => cached, v => cached = v, () => slow.Task.ContinueWith(_ => (string?)null));

        cached = Task.FromResult<string?>("2026.10.05");
        slow.SetResult();

        Assert.IsNull(await slowCall);
        Assert.AreEqual(
            "2026.10.05",
            await YtdlpService.CachedVersionAsync(
                () => cached, v => cached = v,
                () => throw new InvalidOperationException("fresh probe must not run")));
    }
}
