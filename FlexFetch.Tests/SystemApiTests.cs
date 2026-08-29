using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FlexFetch.Tests;

[TestClass]
public sealed class SystemApiTests
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
    public async Task SystemInfo_RequiresLogin()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/system/info");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task SystemInfo_ReturnsOkWithQueueStats()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new { userName = "alice", password = "password1" });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "alice", password = "password1" });
        login.EnsureSuccessStatusCode();

        // Regression: TaskService.QueuedCount used ChannelReader.Count, which
        // throws NotSupportedException for unbounded channels and made this
        // endpoint return 500. It must now return 200 with queue stats.
        var response = await client.GetAsync("/api/system/info");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SystemInfoResponse>();
        Assert.IsNotNull(body);
        Assert.IsGreaterThanOrEqualTo(0, body.QueuedTasks);
        Assert.IsGreaterThanOrEqualTo(0, body.RunningTasks);
        Assert.IsGreaterThan(0, body.ConcurrencyLimit);

        // Build metadata injected by the GenerateBuildInfo MSBuild target:
        // version and build configuration are always present; GitLog may be
        // "unknown" when the assembly was built outside a git checkout.
        Assert.IsFalse(string.IsNullOrEmpty(body.Version));
        Assert.IsFalse(string.IsNullOrEmpty(body.GitLog));
        Assert.IsFalse(string.IsNullOrEmpty(body.BuildDateTime));
        Assert.AreNotEqual("unknown", body.BuildConfiguration);
        Assert.IsFalse(string.IsNullOrEmpty(body.PlaywrightVersion));
    }

    private sealed record SystemInfoResponse(
        int QueuedTasks,
        int RunningTasks,
        int ConcurrencyLimit,
        string Version,
        string GitLog,
        string BuildDateTime,
        string BuildConfiguration,
        string PlaywrightVersion);
}
