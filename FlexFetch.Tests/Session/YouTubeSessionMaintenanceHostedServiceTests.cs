using FlexFetch.HostedServices;
using FlexFetch.Services.Session;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class YouTubeSessionMaintenanceHostedServiceTests
{
    [TestMethod]
    public async Task AgedRound_SuccessfulExport_RecordsOkVerdict()
    {
        var (health, detail) = await Run(_ => Task.FromResult(YouTubeSessionHealth.Ok));

        Assert.AreEqual(YouTubeSessionHealth.Ok, health);
        Assert.IsNull(detail);
    }

    [TestMethod]
    public async Task AgedRound_RejectedExport_RecordsRejectionHealth()
    {
        var (health, detail) = await Run(_ =>
            throw new ExportRejectedException(YouTubeSessionHealth.SessionRotated, "the jar was rotated"));

        Assert.AreEqual(YouTubeSessionHealth.SessionRotated, health);
        Assert.AreEqual("the jar was rotated", detail);
    }

    [TestMethod]
    public async Task AgedRound_UnexpectedFailure_RecordsError()
    {
        var (health, detail) = await Run(_ => throw new InvalidOperationException("boom"));

        Assert.AreEqual(YouTubeSessionHealth.Error, health);
        Assert.AreEqual("boom", detail);
    }

    [TestMethod]
    public async Task AgedRound_Cancellation_PassesThrough()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => YouTubeSessionMaintenanceHostedService.RunAgedExportRoundAsync(
                TestLog.Instance,
                ct => throw new OperationCanceledException(ct),
                (_, _) => Assert.Fail("a cancelled round must not record a verdict"),
                cts.Token));
    }

    [TestMethod]
    public void IsUsable_InfoVerdictsCountAsPass()
    {
        // WebBindingOnly is informational (InnerTube accepted, only the web
        // surface declined) - it must not walk the failure counter toward
        // the alert.
        Assert.IsTrue(YouTubeSessionMaintenanceHostedService.IsUsable(YouTubeSessionHealth.Ok));
        Assert.IsTrue(YouTubeSessionMaintenanceHostedService.IsUsable(YouTubeSessionHealth.WebBindingOnly));
        Assert.IsFalse(YouTubeSessionMaintenanceHostedService.IsUsable(YouTubeSessionHealth.BotCheck));
        Assert.IsFalse(YouTubeSessionMaintenanceHostedService.IsUsable(YouTubeSessionHealth.SessionRotated));
        Assert.IsFalse(YouTubeSessionMaintenanceHostedService.IsUsable(YouTubeSessionHealth.JarInconsistent));
        Assert.IsFalse(YouTubeSessionMaintenanceHostedService.IsUsable(YouTubeSessionHealth.LoginRequired));
        Assert.IsFalse(YouTubeSessionMaintenanceHostedService.IsUsable(YouTubeSessionHealth.Error));
        Assert.IsFalse(YouTubeSessionMaintenanceHostedService.IsUsable(YouTubeSessionHealth.Unknown));
    }

    private static async Task<(YouTubeSessionHealth? Health, string? Detail)> Run(
        Func<CancellationToken, Task<YouTubeSessionHealth>> runExport)
    {
        YouTubeSessionHealth? health = null;
        string? detail = null;
        await YouTubeSessionMaintenanceHostedService.RunAgedExportRoundAsync(
            TestLog.Instance,
            runExport,
            (h, d) =>
            {
                health = h;
                detail = d;
            },
            CancellationToken.None);
        return (health, detail);
    }
}
