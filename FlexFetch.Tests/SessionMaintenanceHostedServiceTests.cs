using FlexFetch.HostedServices;
using FlexFetch.Services.Session;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class SessionMaintenanceHostedServiceTests
{
    [TestMethod]
    public async Task AgedRound_SuccessfulExport_RecordsOkVerdict()
    {
        var (health, detail) = await Run(_ => Task.FromResult(SessionHealth.Ok));

        Assert.AreEqual(SessionHealth.Ok, health);
        Assert.IsNull(detail);
    }

    [TestMethod]
    public async Task AgedRound_RejectedExport_RecordsRejectionHealth()
    {
        var (health, detail) = await Run(_ =>
            throw new ExportRejectedException(SessionHealth.SessionRotated, "the jar was rotated"));

        Assert.AreEqual(SessionHealth.SessionRotated, health);
        Assert.AreEqual("the jar was rotated", detail);
    }

    [TestMethod]
    public async Task AgedRound_UnexpectedFailure_RecordsError()
    {
        var (health, detail) = await Run(_ => throw new InvalidOperationException("boom"));

        Assert.AreEqual(SessionHealth.Error, health);
        Assert.AreEqual("boom", detail);
    }

    [TestMethod]
    public async Task AgedRound_Cancellation_PassesThrough()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => SessionMaintenanceHostedService.RunAgedExportRoundAsync(
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
        Assert.IsTrue(SessionMaintenanceHostedService.IsUsable(SessionHealth.Ok));
        Assert.IsTrue(SessionMaintenanceHostedService.IsUsable(SessionHealth.WebBindingOnly));
        Assert.IsFalse(SessionMaintenanceHostedService.IsUsable(SessionHealth.BotCheck));
        Assert.IsFalse(SessionMaintenanceHostedService.IsUsable(SessionHealth.SessionRotated));
        Assert.IsFalse(SessionMaintenanceHostedService.IsUsable(SessionHealth.JarInconsistent));
        Assert.IsFalse(SessionMaintenanceHostedService.IsUsable(SessionHealth.LoginRequired));
        Assert.IsFalse(SessionMaintenanceHostedService.IsUsable(SessionHealth.Error));
        Assert.IsFalse(SessionMaintenanceHostedService.IsUsable(SessionHealth.Unknown));
    }

    private static async Task<(SessionHealth? Health, string? Detail)> Run(
        Func<CancellationToken, Task<SessionHealth>> runExport)
    {
        SessionHealth? health = null;
        string? detail = null;
        await SessionMaintenanceHostedService.RunAgedExportRoundAsync(
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
