using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Services;
using Microsoft.AspNetCore.Http;

namespace FlexFetch.Tests;

/// <summary>
/// Guest session lifecycle: cookie-backed per-browser sessions, id reuse,
/// fresh sessions after expiry, and the idle threshold configuration.
/// </summary>
[TestClass]
public sealed class GuestSessionServiceTests
{
    private const string CookieHeader = "FlexFetch.Guest=";

    private string? _dir;
    private LiteDbStore? _store;
    private IGuestRepository? _guests;
    private TestConfig? _config;
    private GuestSessionService? _service;

    [TestInitialize]
    public void Setup()
    {
        _dir = TestApp.CreateTempDataDir();
        _store = new LiteDbStore(Path.Combine(_dir, "flexfetch.db"));
        _guests = new GuestRepository(_store);
        _config = new TestConfig();
        _service = new GuestSessionService(_guests, _config);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _store?.Dispose();
        if (_dir is not null && Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [TestMethod]
    public void GetOrCreate_CreatesSessionSetsCookieAndReusesIt()
    {
        var first = new DefaultHttpContext();
        var id = _service!.GetOrCreateOwnerId(first);

        // Id shape, server-side record, cookie attributes.
        Assert.IsTrue(id.StartsWith("guest-", StringComparison.Ordinal));
        var record = _guests!.GetById(id);
        Assert.IsNotNull(record);
        var setCookie = first.Response.Headers["Set-Cookie"].ToString();
        Assert.Contains(id, setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);

        // The same browser (cookie present) keeps the same session and the
        // record is touched (activity tracking for the cleanup sweep); no
        // new cookie is issued.
        var before = record!.LastActiveAt;
        var second = new DefaultHttpContext();
        second.Request.Headers["Cookie"] = CookieHeader + id;
        Assert.AreEqual(id, _service.GetOrCreateOwnerId(second));
        Assert.DoesNotContain(id, second.Response.Headers["Set-Cookie"].ToString());
        Assert.IsTrue(_guests.GetById(id)!.LastActiveAt >= before);
    }

    [TestMethod]
    public void GetOrCreate_UnknownSessionCookie_StartsFreshSession()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = CookieHeader + "guest-expired000";

        var id = _service!.GetOrCreateOwnerId(ctx);

        Assert.AreNotEqual("guest-expired000", id);
        Assert.IsNotNull(_guests!.GetById(id));
        Assert.IsNull(_guests.GetById("guest-expired000"));
    }

    [TestMethod]
    public void GetOrCreate_CookieWithoutGuestPrefix_StartsFreshSession()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = CookieHeader + "injected-value";

        var id = _service!.GetOrCreateOwnerId(ctx);

        Assert.IsTrue(id.StartsWith("guest-", StringComparison.Ordinal));
        Assert.IsNotNull(_guests!.GetById(id));
    }

    [TestMethod]
    public void GetExisting_ReturnsSessionOnlyWhenCookieAndRecordExist()
    {
        var none = new DefaultHttpContext();
        Assert.IsNull(_service!.GetExistingOwnerId(none));

        var known = new DefaultHttpContext();
        known.Request.Headers["Cookie"] = CookieHeader + "guest-unknown00";
        Assert.IsNull(_service.GetExistingOwnerId(known));

        var created = new DefaultHttpContext();
        var id = _service.GetOrCreateOwnerId(created);
        var withCookie = new DefaultHttpContext();
        withCookie.Request.Headers["Cookie"] = CookieHeader + id;
        Assert.AreEqual(id, _service.GetExistingOwnerId(withCookie));
    }

    [TestMethod]
    public void IdleThreshold_UsesConfiguredHours()
    {
        // Default: 15 days.
        Assert.AreEqual(TimeSpan.FromHours(360), _service!.IdleThreshold);

        _config!.Set(ConfigKeys.AnonymousSessionHours, "5");
        Assert.AreEqual(TimeSpan.FromHours(5), _service.IdleThreshold);
    }
}
