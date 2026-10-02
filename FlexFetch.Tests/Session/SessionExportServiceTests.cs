using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Session;
using Microsoft.Extensions.Configuration;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class SessionExportServiceTests
{
    private static ProbeResult Probe(params string[] lines) =>
        new(false, YtdlpOutputClass.Unknown, lines, string.Empty);

    [TestMethod]
    public void Describe_LeadsWithErrorThatTrailsWarnings()
    {
        // Incident shape: a missing JS runtime surfaces as WARNINGs while the
        // decisive ERROR (no formats left) comes last - the excerpt must show
        // the ERROR first, not hide it behind the take-limit.
        var probe = Probe(
            "WARNING: [youtube] aqz-KE-bpKQ: n challenge solving failed: ...",
            "WARNING: [youtube] aqz-KE-bpKQ: mweb client https formats require a GVS PO Token ...",
            "WARNING: Only images are available for download. use --list-formats to see them",
            "ERROR: [youtube] aqz-KE-bpKQ: Requested format is not available");
        Assert.AreEqual(
            "ERROR: [youtube] aqz-KE-bpKQ: Requested format is not available"
            + " | WARNING: [youtube] aqz-KE-bpKQ: n challenge solving failed: ..."
            + " | WARNING: [youtube] aqz-KE-bpKQ: mweb client https formats require a GVS PO Token ...",
            SessionExportService.Describe(probe));
    }

    [TestMethod]
    public void Describe_JoinsWarningsWhenNoErrorPresent()
    {
        Assert.AreEqual(
            "WARNING: one | WARNING: two",
            SessionExportService.Describe(Probe("WARNING: one", "WARNING: two")));
    }

    [TestMethod]
    public void Describe_FallsBackToExitFlagWithoutDiagnosticLines()
    {
        Assert.AreEqual("exit=False", SessionExportService.Describe(Probe()));
        Assert.AreEqual(
            "exit=True",
            SessionExportService.Describe(new ProbeResult(true, YtdlpOutputClass.Ok, Array.Empty<string>(), "t")));
    }

    [TestMethod]
    public async Task ImportAsync_EarlyReturn_ResetsPipelineState()
    {
        // A jar with no youtube.com cookies is rejected before the probe - the
        // pipeline must still land back in its idle state (stage, detail, lock).
        var dir = TestApp.CreateTempDataDir();
        ILogger log = TestLog.Instance;
        var proxy = new NoProxyStub();
        var storage = new StorageService(dir);
        var export = new SessionExportService(
            new FirefoxBrowserService(proxy, storage, log),
            new SessionSnapshotService(storage),
            new SessionProbeService(new YtdlpService(proxy, log, dir), proxy, log),
            new ConfigurationBuilder().Build(),
            log);

        var result = await export.ImportAsync("example.com\tTRUE\t/\tFALSE\t0\tA\tB", null);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual(1, result.Parsed);
        Assert.AreEqual(0, result.ImportedToSnapshot);
        Assert.AreEqual("idle", export.CurrentStage);
        Assert.IsNull(export.CurrentDetail);
        Assert.IsFalse(export.IsRunning);
    }

    private sealed class NoProxyStub : IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public bool ShouldProxyFast(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;
    }
}
