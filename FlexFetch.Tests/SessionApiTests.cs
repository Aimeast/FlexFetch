using System.Net;
using System.Net.Http.Json;
using FlexFetch.Config;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FlexFetch.Tests;

/// <summary>
/// The session API surface: routes are segmented by site
/// (/api/session/youtube/* and /api/session/twitter/*) and the response
/// field names are aligned across the two sites (exists/meta/cookieCount/
/// storage). The legacy unprefixed youtube routes must stay gone.
/// </summary>
[TestClass]
public sealed class SessionApiTests
{
    private string? _dataDir;

    [TestInitialize]
    public void Setup() => _dataDir = TestApp.CreateTempDataDir();

    [TestCleanup]
    public void Cleanup()
    {
        if (_dataDir is not null && Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private static async Task LoginAdminAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "admin-pass-1" });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode, "the seeded admin must accept the initial password");
    }

    private WebApplicationFactory<Program> CreateFactory(IReadOnlyDictionary<string, string>? settings = null) =>
        TestApp.CreateFactory(_dataDir!, settings: settings);

    [TestMethod]
    public async Task SessionEndpoints_RequireAdmin()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var anon = await client.GetAsync("/api/session/youtube/status");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anon.StatusCode);
        var anonTwitter = await client.GetAsync("/api/session/twitter/status");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonTwitter.StatusCode);
    }

    [TestMethod]
    public async Task YoutubeStatus_SiteSegmentedShape()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await LoginAdminAsync(client);

        var status = await client.GetAsync("/api/session/youtube/status");
        Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
        var body = await status.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        Assert.IsNotNull(body);
        Assert.AreEqual(false, body["exists"].GetBoolean());
        Assert.AreEqual(0, body["cookieCount"].GetInt32());
        Assert.AreEqual("encrypted v1", body["storage"].GetString());
        Assert.IsTrue(body.ContainsKey("meta"));
        Assert.IsTrue(body.ContainsKey("isRunning"), "the refresh pipeline state stays on the youtube status");

        // The old unprefixed route is gone - the youtube segment is mandatory.
        var legacy = await client.GetAsync("/api/session/export/status");
        Assert.AreEqual(HttpStatusCode.NotFound, legacy.StatusCode);
    }

    [TestMethod]
    public async Task YoutubeImport_EmptyText_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await LoginAdminAsync(client);

        var response = await client.PostAsJsonAsync("/api/session/youtube/import", new { text = "" });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task TwitterStatus_Shape()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await LoginAdminAsync(client);

        var status = await client.GetAsync("/api/session/twitter/status");
        Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
        var body = await status.Content.ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        Assert.IsNotNull(body);
        Assert.AreEqual(false, body["exists"].GetBoolean());
        Assert.AreEqual(0, body["cookieCount"].GetInt32());
        Assert.AreEqual("encrypted v1", body["storage"].GetString());
    }

    [TestMethod]
    public async Task LegacyYoutubeRoutes_AreGone()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await LoginAdminAsync(client);

        foreach (var legacy in new[] { "/api/session/import", "/api/session/canary", "/api/session/pot-status", "/api/session/diagnose" })
        {
            var response = await client.GetAsync(legacy);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, legacy);
        }
    }
}
