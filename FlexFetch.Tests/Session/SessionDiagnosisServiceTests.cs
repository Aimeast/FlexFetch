using FlexFetch.Config;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Session;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class SessionDiagnosisServiceTests
{
    private static readonly ILogger Log = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();

    private static ProbeResult Result(YtdlpOutputClass cls, bool success) =>
        new(success, cls, success ? Array.Empty<string>() : new[] { "ERROR: injected" }, "Probe Video");

    private static SessionDiagnosisService CreateService(
        SessionSnapshotService snapshot,
        Func<string, string?, string, CancellationToken, Task<ProbeResult>> runner) =>
        new(snapshot, new SessionProbeService(
                new YtdlpService(new TestProxy(), Log, TestApp.CreateTempDataDir()),
                new TestProxy(), Log),
            new TestConfig(), Log, runner);

    [TestMethod]
    public async Task RunAsync_NoSnapshot_RunsAnonymousItemsOnly()
    {
        var snapshot = new SessionSnapshotService(new StorageService(TestApp.CreateTempDataDir()));
        var seen = new List<(string? Cookie, string Args)>();
        var service = CreateService(snapshot, (url, cookieFile, args, ct) =>
        {
            seen.Add((cookieFile, args));
            return Task.FromResult(Result(YtdlpOutputClass.BotCheck, false));
        });

        var report = await service.RunAsync(CancellationToken.None);

        Assert.HasCount(2, report.Items);
        Assert.HasCount(2, seen);
        Assert.IsTrue(seen.All(s => s.Cookie is null), "anonymous items must not touch any cookie file");
        Assert.IsTrue(report.Items.All(i => i.Mode == "anonymous"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(report.Resolution));
        Assert.IsNotNull(service.Last);
        Assert.AreEqual(service.Last, report);
    }

    [TestMethod]
    public async Task RunAsync_WithSnapshot_RunsSessionItemsOnCopiesAndStoresReport()
    {
        var snapshot = new SessionSnapshotService(new StorageService(TestApp.CreateTempDataDir()));
        snapshot.WriteSnapshot(
            new List<CookieItem> { new() { Domain = ".youtube.com", Name = "SID", Value = "s" } },
            new SessionMeta { VisitorData = "visitor-1" });
        var snapshotPath = Path.Combine(Path.GetDirectoryName(snapshot.SnapshotPath)!, "session.txt");
        var snapshotContent = File.ReadAllText(snapshotPath);

        string? sessionCookieFile = null;
        var service = CreateService(snapshot, (url, cookieFile, args, ct) =>
        {
            if (cookieFile is not null)
            {
                sessionCookieFile = cookieFile;
                // android/tv clients with cookies trigger the bot wall; only
                // mweb works with the session (the guide's measured posture).
                return Task.FromResult(args.Contains("android_vr", StringComparison.Ordinal)
                    ? Result(YtdlpOutputClass.BotCheck, false)
                    : Result(YtdlpOutputClass.Ok, true));
            }

            return Task.FromResult(Result(YtdlpOutputClass.BotCheck, false));
        });

        var report = await service.RunAsync(CancellationToken.None);

        Assert.HasCount(4, report.Items);
        Assert.IsTrue(report.Items.Any(i => i.Mode == "session" && i.Healthy));
        Assert.IsTrue(report.Items.Any(i => i.Mode == "anonymous" && !i.Healthy));
        StringAssert.Contains(report.Resolution, "Session mode works on mweb");

        // The session item ran on a COPY; the snapshot itself is untouched.
        Assert.IsNotNull(sessionCookieFile);
        Assert.AreNotEqual(snapshotPath, sessionCookieFile);
        Assert.IsFalse(File.Exists(sessionCookieFile), "the copy must be deleted after the run");
        Assert.AreEqual(snapshotContent, File.ReadAllText(snapshotPath));

        // The report is stored for the status page (GET /api/session/diagnose).
        Assert.AreSame(report, service.Last);
        Assert.IsTrue(report.RanAt <= DateTime.UtcNow);
    }

    [TestMethod]
    public async Task RunAsync_ConcurrentSecondCall_IsRejected()
    {
        var snapshot = new SessionSnapshotService(new StorageService(TestApp.CreateTempDataDir()));
        var blocker = new TaskCompletionSource<ProbeResult>();
        var service = CreateService(snapshot, (url, cookieFile, args, ct) => blocker.Task);

        var first = service.RunAsync(CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.RunAsync(CancellationToken.None));
        Assert.IsTrue(service.IsRunning);

        blocker.SetResult(Result(YtdlpOutputClass.Ok, true));
        await first;
        Assert.IsFalse(service.IsRunning);
    }

    [TestMethod]
    public void BuildResolution_CoversFailurePaths()
    {
        var ok = new DiagnoseItem("session", "mweb", true, "Ok", true, "t");
        var bot = new DiagnoseItem("anonymous", "android_vr,mweb", false, "BotCheck", false, "b");
        var failed = new DiagnoseItem("session", "mweb", false, "SessionRotated", false, "r");
        var anonOk = new DiagnoseItem("anonymous", "mweb", true, "Ok", true, "t");

        // Anonymous works, no snapshot: no session needed.
        StringAssert.Contains(SessionDiagnosisService.BuildResolution(
            new[] { bot, new DiagnoseItem("anonymous", "mweb", true, "Ok", true, "t") }), "no session needed");

        // Anonymous mweb works, session fails: import a fresh jar.
        StringAssert.Contains(SessionDiagnosisService.BuildResolution(
            new[] { failed, bot, anonOk }), "import a fresh session");

        // Everything fails: check the network first.
        StringAssert.Contains(SessionDiagnosisService.BuildResolution(
            new[] { failed, bot }), "network/proxy");
    }

    private sealed class TestProxy : Services.Routing.IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public bool ShouldProxyFast(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;

        public string? GetBrowserProxyAddress() => null;
    }
}
