using FlexFetch.HostedServices;

namespace FlexFetch.Tests;

[TestClass]
public sealed class DependencyUpgradeHostedServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    [TestMethod]
    public void IsUpgradeDue_MissingBinary_IsNeverDue()
    {
        // A missing binary belongs to the startup install path; the upgrade
        // round must leave it alone.
        Assert.IsFalse(DependencyUpgradeHostedService.IsUpgradeDue(
            binaryExists: false, lastUpgradeUtc: null, binaryModifiedUtc: null, Now, Day));
    }

    [TestMethod]
    public void IsUpgradeDue_NoRecord_FallsBackToBinaryMtime()
    {
        // A binary written 23 hours ago is inside the 24 hour period...
        Assert.IsFalse(DependencyUpgradeHostedService.IsUpgradeDue(
            true, null, Now - TimeSpan.FromHours(23), Now, Day));
        // ...one written days ago is overdue. This is the first boot after
        // a redeploy: the persistent volume keeps the old binary mtime and
        // the update happens immediately instead of a period later.
        Assert.IsTrue(DependencyUpgradeHostedService.IsUpgradeDue(
            true, null, Now - TimeSpan.FromDays(2), Now, Day));
    }

    [TestMethod]
    public void IsUpgradeDue_RecordWinsOverBinaryMtime()
    {
        // The persisted record is the authority: an old record is due even
        // when something touched the binary recently.
        Assert.IsTrue(DependencyUpgradeHostedService.IsUpgradeDue(
            true, Now - TimeSpan.FromDays(2), Now - TimeSpan.FromHours(1), Now, Day));
        // A fresh record keeps a long-untouched binary quiet - a check that
        // found nothing new still counts as an update round.
        Assert.IsFalse(DependencyUpgradeHostedService.IsUpgradeDue(
            true, Now - TimeSpan.FromHours(1), Now - TimeSpan.FromDays(30), Now, Day));
    }

    [TestMethod]
    public void IsUpgradeDue_ExactlyOneInterval_IsDue()
    {
        // Rounds are spaced by the interval itself, so the boundary must be
        // inclusive: a strict comparison would systematically skip the
        // round right after a check.
        Assert.IsTrue(DependencyUpgradeHostedService.IsUpgradeDue(
            true, Now - Day, null, Now, Day));
    }

    [TestMethod]
    public async Task UpgradeRound_RunsOnlyDueComponents_AndRecordsSuccess()
    {
        var dir = TestApp.CreateTempDataDir();
        var statePath = Path.Combine(dir, DependencyUpgradeHostedService.StateFileName);
        var freshPath = Path.Combine(dir, "fresh.bin");
        var stalePath = Path.Combine(dir, "stale.bin");
        File.WriteAllBytes(freshPath, new byte[] { 1 });
        File.WriteAllBytes(stalePath, new byte[] { 2 });
        File.SetLastWriteTimeUtc(freshPath, Now);
        File.SetLastWriteTimeUtc(stalePath, Now - TimeSpan.FromDays(5));

        var ran = new List<string>();
        await DependencyUpgradeHostedService.RunUpgradeRoundAsync(
            new (string Name, string BinaryPath, Func<Task> Upgrade)[]
            {
                ("missing", Path.Combine(dir, "absent.bin"), Record(ran, "missing")),
                ("fresh", freshPath, Record(ran, "fresh")),
                ("stale", stalePath, Record(ran, "stale")),
            },
            statePath, Day, Now,
            due: _ => { },
            failure: (_, _) => throw new InvalidOperationException("nothing may fail here"));

        CollectionAssert.AreEqual(new[] { "stale" }, ran);
        var records = DependencyUpgradeHostedService.LoadLastUpgradeRecords(statePath);
        CollectionAssert.AreEquivalent(new[] { "stale" }, records.Keys);
        Assert.AreEqual(Now, records["stale"]);
    }

    [TestMethod]
    public async Task UpgradeRound_FailedUpgradeIsIsolatedAndNotRecorded()
    {
        var dir = TestApp.CreateTempDataDir();
        var statePath = Path.Combine(dir, DependencyUpgradeHostedService.StateFileName);
        var stalePath = Path.Combine(dir, "stale.bin");
        File.WriteAllBytes(stalePath, new byte[] { 1 });
        File.SetLastWriteTimeUtc(stalePath, Now - TimeSpan.FromDays(5));

        var failures = new List<string>();
        await DependencyUpgradeHostedService.RunUpgradeRoundAsync(
            new (string Name, string BinaryPath, Func<Task> Upgrade)[]
            {
                ("broken", stalePath, () => throw new InvalidOperationException("network down")),
                ("working", stalePath, () => Task.CompletedTask),
            },
            statePath, Day, Now,
            due: _ => { },
            failure: (name, _) => failures.Add(name));

        CollectionAssert.AreEqual(new[] { "broken" }, failures);
        var records = DependencyUpgradeHostedService.LoadLastUpgradeRecords(statePath);
        CollectionAssert.AreEquivalent(new[] { "working" }, records.Keys);
    }

    [TestMethod]
    public void UpgradeRecords_RoundTrip_AndTolerateGarbage()
    {
        var dir = TestApp.CreateTempDataDir();
        var path = Path.Combine(dir, "state.json");
        DependencyUpgradeHostedService.SaveLastUpgradeRecords(
            path, new Dictionary<string, DateTime> { ["yt-dlp"] = Now });

        var loaded = DependencyUpgradeHostedService.LoadLastUpgradeRecords(path);
        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual(Now, loaded["yt-dlp"]);

        // A corrupt or missing file must not take the round down: the
        // binary mtime fallback carries the decision instead.
        File.WriteAllText(path, "{ not json");
        Assert.HasCount(0, DependencyUpgradeHostedService.LoadLastUpgradeRecords(path));
        Assert.HasCount(0, DependencyUpgradeHostedService.LoadLastUpgradeRecords(
            Path.Combine(dir, "absent.json")));
    }

    private static Func<Task> Record(List<string> ran, string name) =>
        () =>
        {
            ran.Add(name);
            return Task.CompletedTask;
        };
}
