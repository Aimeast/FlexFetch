using System.Net;
using System.Net.Http.Json;

namespace FlexFetch.Tests;

[TestClass]
public sealed class AuthApiTests
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

    [TestMethod]
    public async Task RegisterAndLogin_FlowWorks()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync("/api/auth/register", new { userName = "alice", password = "password1" });
        Assert.AreEqual(HttpStatusCode.OK, register.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "alice", password = "password1" });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual("alice", body.UserName);
        Assert.AreEqual("User", body.Role);
    }

    [TestMethod]
    public async Task Login_WrongPassword_Returns401()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new { userName = "bob", password = "password1" });

        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "bob", password = "wrong" });

        Assert.AreEqual(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [TestMethod]
    public async Task Me_ReturnsCurrentUserWithRole()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();

        // Anonymous: 401.
        var anon = await client.GetAsync("/api/auth/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anon.StatusCode);

        // Regular user: returns own name and role (drives UI navigation).
        await client.PostAsJsonAsync("/api/auth/register", new { userName = "eve", password = "password1" });
        await client.PostAsJsonAsync("/api/auth/login", new { userName = "eve", password = "password1" });
        var me = await client.GetAsync("/api/auth/me");
        Assert.AreEqual(HttpStatusCode.OK, me.StatusCode);
        var meBody = await me.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.IsNotNull(meBody);
        Assert.AreEqual("eve", meBody.UserName);
        Assert.AreEqual("User", meBody.Role);

        // Admin: role is Admin.
        using var adminClient = factory.CreateClient();
        await adminClient.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "admin-pass-1" });
        var adminMe = await adminClient.GetAsync("/api/auth/me");
        Assert.AreEqual(HttpStatusCode.OK, adminMe.StatusCode);
        var adminBody = await adminMe.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.IsNotNull(adminBody);
        Assert.AreEqual("Admin", adminBody.Role);
    }

    [TestMethod]
    public async Task AdminEndpoints_RequireAdminRole()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();

        // Anonymous: 401 (not redirected).
        var anon = await client.GetAsync("/api/users/pending");
        Assert.AreEqual(HttpStatusCode.Unauthorized, anon.StatusCode);

        // Regular user: 403.
        await client.PostAsJsonAsync("/api/auth/register", new { userName = "carol", password = "password1" });
        await client.PostAsJsonAsync("/api/auth/login", new { userName = "carol", password = "password1" });
        var asUser = await client.GetAsync("/api/users/pending");
        Assert.AreEqual(HttpStatusCode.Forbidden, asUser.StatusCode);

        // Admin: 200.
        using var adminClient = factory.CreateClient();
        await adminClient.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "admin-pass-1" });
        var asAdmin = await adminClient.GetAsync("/api/users/pending");
        Assert.AreEqual(HttpStatusCode.OK, asAdmin.StatusCode);
    }

    [TestMethod]
    public async Task ApprovalFlow_EndToEnd()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();

        // Open registration by default; switch policy via config is not exposed
        // yet in the API, so verify approval flow through the service-level
        // behavior indirectly: admin can disable, and disabled users are blocked.
        await client.PostAsJsonAsync("/api/auth/register", new { userName = "dave", password = "password1" });

        using var adminClient = factory.CreateClient();
        await adminClient.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "admin-pass-1" });

        // Admin sees pending list (empty in open policy) - 200.
        var pending = await adminClient.GetAsync("/api/users/pending");
        Assert.AreEqual(HttpStatusCode.OK, pending.StatusCode);

        // Login as dave to get id via response, then disable via admin.
        var daveLogin = await client.PostAsJsonAsync("/api/auth/login", new { userName = "dave", password = "password1" });
        var dave = await daveLogin.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.IsNotNull(dave);

        var disable = await adminClient.PostAsync($"/api/users/{dave.Id}/disable", null);
        Assert.AreEqual(HttpStatusCode.OK, disable.StatusCode);

        // Disabled user cannot log in anymore.
        var blocked = await client.PostAsJsonAsync("/api/auth/login", new { userName = "dave", password = "password1" });
        Assert.AreEqual(HttpStatusCode.Forbidden, blocked.StatusCode);
    }

    [TestMethod]
    public async Task Logout_EndsSession()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new { userName = "erin", password = "password1" });
        await client.PostAsJsonAsync("/api/auth/login", new { userName = "erin", password = "password1" });

        // Authenticated call works (e.g. root stays public, so use pending via
        // role check is 403 for user - but 401 means session gone).
        var before = await client.GetAsync("/api/users/pending");
        Assert.AreEqual(HttpStatusCode.Forbidden, before.StatusCode);

        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.AreEqual(HttpStatusCode.OK, logout.StatusCode);

        var after = await client.GetAsync("/api/users/pending");
        Assert.AreEqual(HttpStatusCode.Unauthorized, after.StatusCode);
    }
}
