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
    public void SelectBtbnAsset_PicksNewestReleaseBranchBuild()
    {
        const string json = """
            {"tag_name":"latest","assets":[
                {"name":"ffmpeg-master-latest-linux64-gpl.tar.xz","browser_download_url":"https://example.com/master.tar.xz"},
                {"name":"ffmpeg-n8.1-latest-linux64-gpl-8.1.tar.xz","browser_download_url":"https://example.com/n8.1.tar.xz"},
                {"name":"ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz","browser_download_url":"https://example.com/n9.0.tar.xz"},
                {"name":"ffmpeg-n9.0-latest-linux64-gpl-shared-9.0.tar.xz","browser_download_url":"https://example.com/n9.0-shared.tar.xz"},
                {"name":"ffmpeg-n9.0-latest-linuxarm64-gpl-9.0.tar.xz","browser_download_url":"https://example.com/n9.0-arm64.tar.xz"},
                {"name":"ffmpeg-n9.0-latest-win64-gpl-9.0.zip","browser_download_url":"https://example.com/n9.0-win.zip"}
            ]}
            """;

        var x64 = YtdlpService.SelectBtbnAsset(json, isArm64: false);
        Assert.IsNotNull(x64);
        Assert.AreEqual("9.0", x64.BranchVersion);
        Assert.AreEqual("https://example.com/n9.0.tar.xz", x64.Url,
            "the newest release branch, static (not shared), gpl, linux64");

        var arm64 = YtdlpService.SelectBtbnAsset(json, isArm64: true);
        Assert.IsNotNull(arm64);
        Assert.AreEqual("https://example.com/n9.0-arm64.tar.xz", arm64.Url);
    }

    [TestMethod]
    public void SelectBtbnAsset_NoReleaseBranchBuild_ReturnsNull()
    {
        // Master-only releases offer no versioned branch to pin: null makes
        // the caller fail loudly instead of installing a rolling build.
        const string json = """
            {"assets":[
                {"name":"ffmpeg-master-latest-linux64-gpl.tar.xz","browser_download_url":"https://example.com/master.tar.xz"}
            ]}
            """;
        Assert.IsNull(YtdlpService.SelectBtbnAsset(json, isArm64: false));
        Assert.IsNull(YtdlpService.SelectBtbnAsset("""{"assets":[]}""", isArm64: false));
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
