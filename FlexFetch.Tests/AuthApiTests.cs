using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlexFetch.Data;
using Microsoft.Extensions.DependencyInjection;

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
        await LoginAdminAsync(factory.Services, adminClient);
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
        await LoginAdminAsync(factory.Services, adminClient);
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
        await LoginAdminAsync(factory.Services, adminClient);

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
    public async Task DisabledUser_ExistingCookieIsRejectedImmediately()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new { userName = "frank", password = "password1" });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "frank", password = "password1" });
        var body = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        using var adminClient = factory.CreateClient();
        await LoginAdminAsync(factory.Services, adminClient);
        var disable = await adminClient.PostAsync($"/api/users/{body.Id}/disable", null);
        Assert.AreEqual(HttpStatusCode.OK, disable.StatusCode);

        // The cookie is a cached snapshot; the next request re-checks the
        // store and rejects the disabled account.
        var after = await client.GetAsync("/api/auth/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [TestMethod]
    public async Task DeleteUser_RemovesOwnedTasksAndInvalidatesSession()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new { userName = "grace", password = "password1" });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "grace", password = "password1" });
        var body = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.IsNotNull(body);

        var submit = await client.PostAsJsonAsync("/api/tasks", new { url = "https://example.com/a.bin" });
        Assert.AreEqual(HttpStatusCode.OK, submit.StatusCode);
        var created = await submit.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = created.GetProperty("id").GetString();
        Assert.IsNotNull(taskId);

        using var adminClient = factory.CreateClient();
        await LoginAdminAsync(factory.Services, adminClient);
        var delete = await adminClient.DeleteAsync($"/api/users/{body.Id}");
        Assert.AreEqual(HttpStatusCode.OK, delete.StatusCode);

        var tasks = factory.Services.GetRequiredService<ITaskRepository>();
        Assert.IsNull(tasks.GetById(taskId), "the deleted account's task groups must be removed");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [TestMethod]
    public async Task LastActiveAdmin_CannotBeDisabledOrDeleted()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var adminClient = factory.CreateClient();
        var body = await LoginAdminAsync(factory.Services, adminClient);

        var disable = await adminClient.PostAsync($"/api/users/{body.Id}/disable", null);
        Assert.AreEqual(HttpStatusCode.Conflict, disable.StatusCode);

        var delete = await adminClient.DeleteAsync($"/api/users/{body.Id}");
        Assert.AreEqual(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [TestMethod]
    public async Task NullCredentials_ReturnClientErrors()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync(
            "/api/auth/register", new { userName = (string?)null, password = "password1" });
        Assert.AreEqual(HttpStatusCode.BadRequest, register.StatusCode);

        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = (string?)null, password = "password1" });
        Assert.AreEqual(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [TestMethod]
    public async Task LoginAttempts_AreRateLimited()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();
        HttpStatusCode last = default;

        for (var i = 0; i < 21; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/login", new { userName = "nobody", password = "wrong" });
            last = response.StatusCode;
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, last);
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

    [TestMethod]
    public async Task ChangePassword_EndToEnd()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new { userName = "alice", password = "password1" });
        await client.PostAsJsonAsync("/api/auth/login", new { userName = "alice", password = "password1" });

        // Wrong current password: 401, password unchanged.
        var wrong = await client.PostAsJsonAsync(
            "/api/auth/change-password", new { currentPassword = "wrong", newPassword = "newpass1" });
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // Too-short new password: 400.
        var shortPw = await client.PostAsJsonAsync(
            "/api/auth/change-password", new { currentPassword = "password1", newPassword = "abcd" });
        Assert.AreEqual(HttpStatusCode.BadRequest, shortPw.StatusCode);

        // Success, then only the new password logs in (on a fresh client).
        var ok = await client.PostAsJsonAsync(
            "/api/auth/change-password", new { currentPassword = "password1", newPassword = "newpass1" });
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);

        using var fresh = factory.CreateClient();
        var oldLogin = await fresh.PostAsJsonAsync("/api/auth/login", new { userName = "alice", password = "password1" });
        Assert.AreEqual(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        var newLogin = await fresh.PostAsJsonAsync("/api/auth/login", new { userName = "alice", password = "newpass1" });
        Assert.AreEqual(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [TestMethod]
    public async Task ChangePassword_RequiresAuthentication()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();

        var anon = await client.PostAsJsonAsync(
            "/api/auth/change-password", new { currentPassword = "x", newPassword = "yyyyyy" });

        Assert.AreEqual(HttpStatusCode.Unauthorized, anon.StatusCode);
    }

    [TestMethod]
    public async Task Login_IssuesPersistentCookie_ForDefaultSessionHours()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/auth/register", new { userName = "alice", password = "password1" });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "alice", password = "password1" });

        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        var expires = GetAuthCookieExpires(login);
        Assert.IsNotNull(expires, "the auth cookie must carry an explicit expiry (persistent, survives browser restart)");
        AssertNear(expires.Value, DateTimeOffset.UtcNow.AddHours(168));
    }

    [TestMethod]
    public async Task Login_CookieLifetime_FollowsSessionHoursConfig()
    {
        using var factory = TestApp.CreateFactory(_dataDir!, settings: new Dictionary<string, string>
        {
            ["account:sessionHours"] = "720",
        });
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/auth/register", new { userName = "bob", password = "password1" });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "bob", password = "password1" });

        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        var expires = GetAuthCookieExpires(login);
        Assert.IsNotNull(expires, "the auth cookie must carry an explicit expiry (persistent, survives browser restart)");
        AssertNear(expires.Value, DateTimeOffset.UtcNow.AddHours(720));
    }

    private static void AssertNear(DateTimeOffset actual, DateTimeOffset expected)
    {
        Assert.IsTrue(Math.Abs((actual - expected).TotalMinutes) < 5,
            $"Cookie expiry {actual:O} is not near the expected {expected:O}");
    }

    private static DateTimeOffset? GetAuthCookieExpires(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        foreach (var cookie in cookies)
        {
            if (!cookie.StartsWith("FlexFetch.Auth=", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var part in cookie.Split(';'))
            {
                var attribute = part.Trim();
                if (attribute.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = attribute["expires=".Length..];
                    return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The initial admin is created by a hosted service after the host
    /// starts, so a test can reach the first request before it exists. Wait
    /// for the record (no credential check) and then log in once so retries
    /// never consume the auth rate limit.
    /// </summary>
    private static async Task<LoginResponse> LoginAdminAsync(IServiceProvider services, HttpClient client)
    {
        var users = services.GetRequiredService<IUserRepository>();
        for (var i = 0; i < 100 && users.GetByUserName("admin") is null; i++)
        {
            await Task.Delay(50);
        }

        Assert.IsNotNull(users.GetByUserName("admin"), "the initial admin account was not created in time");
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = "admin", password = "admin-pass-1" });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.IsNotNull(body);
        return body;
    }

    [TestMethod]
    public async Task Status_ReportsAuthenticationAndAnonymousFlag()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();

        var anon = await client.GetFromJsonAsync<StatusResponse>("/api/auth/status");
        Assert.IsNotNull(anon);
        Assert.IsFalse(anon.Authenticated);
        Assert.IsNull(anon.UserName);
        Assert.IsFalse(anon.AnonymousEnabled);

        await client.PostAsJsonAsync("/api/auth/register", new { userName = "alice", password = "password1" });
        await client.PostAsJsonAsync("/api/auth/login", new { userName = "alice", password = "password1" });
        var signedIn = await client.GetFromJsonAsync<StatusResponse>("/api/auth/status");
        Assert.IsNotNull(signedIn);
        Assert.IsTrue(signedIn.Authenticated);
        Assert.AreEqual("alice", signedIn.UserName);
        Assert.AreEqual("User", signedIn.Role);
        Assert.IsFalse(signedIn.AnonymousEnabled);
    }
}
