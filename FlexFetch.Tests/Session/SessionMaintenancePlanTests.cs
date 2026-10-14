using FlexFetch.Services.Session;

namespace FlexFetch.Tests;

[TestClass]
public sealed class SessionMaintenancePlanTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    [TestMethod]
    public void Decide_WithoutSession_Idles()
    {
        Assert.AreEqual(SessionMaintenanceAction.Idle, SessionMaintenancePlan.Decide(false, null, Interval));
        Assert.AreEqual(SessionMaintenanceAction.Idle, SessionMaintenancePlan.Decide(false, TimeSpan.FromHours(99), Interval));
    }

    [TestMethod]
    public void Decide_YoungSnapshot_LightCheck()
    {
        Assert.AreEqual(SessionMaintenanceAction.LightCheck,
            SessionMaintenancePlan.Decide(true, TimeSpan.FromHours(11.9), Interval));
        Assert.AreEqual(SessionMaintenanceAction.LightCheck,
            SessionMaintenancePlan.Decide(true, TimeSpan.Zero, Interval));
    }

    [TestMethod]
    public void Decide_AgedSnapshot_FullRefresh_AtExactIntervalBoundary()
    {
        // The boundary is INCLUSIVE: exactly the interval counts as aged -
        // with period == snapshot age every round must take the full path,
        // otherwise the light path would starve it forever (the "first
        // check pending" incident).
        Assert.AreEqual(SessionMaintenanceAction.FullRefresh,
            SessionMaintenancePlan.Decide(true, TimeSpan.FromHours(12), Interval));
        Assert.AreEqual(SessionMaintenanceAction.FullRefresh,
            SessionMaintenancePlan.Decide(true, TimeSpan.FromHours(12.1), Interval));
    }

    [TestMethod]
    public void Decide_UnreadableAge_LightCheck()
    {
        // A null age on an existing session (unreadable file) keeps the
        // light path: the copy build fails there and the verdict surfaces
        // through the export pipeline instead.
        Assert.AreEqual(SessionMaintenanceAction.LightCheck,
            SessionMaintenancePlan.Decide(true, null, Interval));
    }
}
