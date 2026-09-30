using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlexFetch.Tests;

/// <summary>
/// Anonymous guest access: when account.allowAnonymous is enabled, visitors
/// without an account can use the task API through a private per-browser
/// guest session (FlexFetch.Guest cookie). Sessions never share tasks with
/// each other, with signed-in users, or vice versa; account and admin
/// endpoints stay closed to guests.
/// </summary>
[TestClass]
public sealed class AnonymousAccessTests
{
    private const string GuestCookieHeader = "FlexFetch.Guest=";

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
    public async Task DisabledByDefault_TasksStillRequireLogin()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();

        var list = await client.GetAsync("/api/tasks");
        Assert.AreEqual(HttpStatusCode.Unauthorized, list.StatusCode);

        var status = await client.GetFromJsonAsync<StatusResponse>("/api/auth/status");
        Assert.IsNotNull(status);
        Assert.IsFalse(status.AnonymousEnabled);
    }

    [TestMethod]
    public async Task Enabled_GuestSessionsArePrivate_PerBrowser()
    {
        using var factory = TestApp.CreateFactory(_dataDir!, allowAnonymous: true);

        // Guest one submits a task; the response establishes their session cookie.
        var guest1 = factory.CreateClient();
        var submit1 = await guest1.PostAsJsonAsync("/api/tasks", new { url = "https://example.com/a.bin" });
        Assert.AreEqual(HttpStatusCode.OK, submit1.StatusCode);
        var created1 = await submit1.Content.ReadFromJsonAsync<GuestSubmitResponse>();
        var cookie1 = GetGuestCookie(submit1);
        Assert.IsNotNull(cookie1);

        // A fresh visitor sees no tasks and is not given a session just by looking.
        var guest2 = factory.CreateClient();
        var emptyList = await guest2.GetAsync("/api/tasks");
        Assert.AreEqual(HttpStatusCode.OK, emptyList.StatusCode);
        var emptyBody = await emptyList.Content.ReadFromJsonAsync<List<GuestTaskDto>>();
        Assert.IsNotNull(emptyBody);
        Assert.HasCount(0, emptyBody);
        Assert.IsNull(GetGuestCookie(emptyList));

        // Guest two submits their own task under their own session.
        var submit2 = await guest2.PostAsJsonAsync("/api/tasks", new { url = "https://example.com/b.bin" });
        Assert.AreEqual(HttpStatusCode.OK, submit2.StatusCode);
        var created2 = await submit2.Content.ReadFromJsonAsync<GuestSubmitResponse>();
        var cookie2 = GetGuestCookie(submit2);
        Assert.IsNotNull(cookie2);
        Assert.AreNotEqual(cookie1, cookie2);

        // Each guest sees only their own task.
        guest1.DefaultRequestHeaders.Add("Cookie", cookie1);
        var list1 = await guest1.GetFromJsonAsync<List<GuestTaskDto>>("/api/tasks");
        Assert.IsNotNull(list1);
        Assert.IsTrue(list1.Any(t => t.Id == created1!.Id));
        Assert.IsFalse(list1.Any(t => t.Id == created2!.Id));

        guest2.DefaultRequestHeaders.Add("Cookie", cookie2);
        var list2 = await guest2.GetFromJsonAsync<List<GuestTaskDto>>("/api/tasks");
        Assert.IsNotNull(list2);
        Assert.IsTrue(list2.Any(t => t.Id == created2!.Id));
        Assert.IsFalse(list2.Any(t => t.Id == created1!.Id));

        // A signed-in user sees neither guest's tasks.
        using var userClient = factory.CreateClient();
        await userClient.PostAsJsonAsync("/api/auth/register", new { userName = "alice", password = "password1" });
        await userClient.PostAsJsonAsync("/api/auth/login", new { userName = "alice", password = "password1" });
        var userList = await userClient.GetFromJsonAsync<List<GuestTaskDto>>("/api/tasks");
        Assert.IsNotNull(userList);
        Assert.IsFalse(userList.Any(t => t.Id == created1!.Id || t.Id == created2!.Id));
    }

    [TestMethod]
    public async Task Enabled_GuestFileAccess_OwnYes_ForeignNo()
    {
        using var factory = TestApp.CreateFactory(_dataDir!, allowAnonymous: true);

        // Guest one's session id comes from their cookie; seed a completed
        // file download owned by that session.
        var guest1 = factory.CreateClient();
        var submit = await guest1.PostAsJsonAsync("/api/tasks", new { url = "https://example.com/a.bin" });
        var cookie1 = GetGuestCookie(submit);
        Assert.IsNotNull(cookie1);
        var guestId = cookie1[GuestCookieHeader.Length..];
        var taskId = SeedCompletedTask(factory, guestId, "report.pdf", "PDF-CONTENT");

        // Owner downloads fine; a second guest cannot; owner can delete.
        guest1.DefaultRequestHeaders.Add("Cookie", cookie1);
        var file = await guest1.GetAsync($"/api/tasks/{taskId}/file");
        Assert.AreEqual(HttpStatusCode.OK, file.StatusCode);
        Assert.AreEqual("PDF-CONTENT", await file.Content.ReadAsStringAsync());

        var guest2 = factory.CreateClient();
        var other = await guest2.PostAsJsonAsync("/api/tasks", new { url = "https://example.com/b.bin" });
        var cookie2 = GetGuestCookie(other);
        Assert.IsNotNull(cookie2);
        guest2.DefaultRequestHeaders.Add("Cookie", cookie2);
        var foreign = await guest2.GetAsync($"/api/tasks/{taskId}/file");
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);

        var delete = await guest1.DeleteAsync($"/api/tasks/{taskId}");
        Assert.AreEqual(HttpStatusCode.OK, delete.StatusCode);
        var gone = await guest1.GetAsync($"/api/tasks/{taskId}/file");
        Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [TestMethod]
    public async Task Enabled_AccountAndAdminEndpointsStayClosed()
    {
        using var factory = TestApp.CreateFactory(_dataDir!, allowAnonymous: true);
        using var guest = factory.CreateClient();

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/auth/me")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/users/pending")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/system/info")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/session/export/status")).StatusCode);
        var password = await guest.PostAsJsonAsync(
            "/api/auth/change-password", new { currentPassword = "x", newPassword = "yyyyyy" });
        Assert.AreEqual(HttpStatusCode.Unauthorized, password.StatusCode);
    }

    [TestMethod]
    public async Task Enabled_StatusReportsAnonymousFlag()
    {
        using var factory = TestApp.CreateFactory(_dataDir!, allowAnonymous: true);
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<StatusResponse>("/api/auth/status");

        Assert.IsNotNull(status);
        Assert.IsFalse(status.Authenticated);
        Assert.IsTrue(status.AnonymousEnabled);
    }

    private static string? GetGuestCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        return values
            .FirstOrDefault(v => v.StartsWith(GuestCookieHeader, StringComparison.Ordinal))
            ?.Split(';')[0];
    }

    private string SeedCompletedTask(WebApplicationFactory<Program> factory, string ownerUserId, string fileName, string content)
    {
        var taskId = FlexFetch.Entities.RandomId.New();
        var filesDir = Path.Combine(_dataDir!, "files", taskId);
        Directory.CreateDirectory(filesDir);
        File.WriteAllText(Path.Combine(filesDir, fileName), content);

        using var scope = factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<FlexFetch.Data.ITaskRepository>();
        tasks.Insert(new FlexFetch.Entities.TaskItem
        {
            Id = taskId,
            OwnerUserId = ownerUserId,
            Url = "https://example.com/report.pdf",
            FileName = fileName,
            Status = FlexFetch.Enums.TaskStatus.Completed,
            Progress = 100,
        });
        return taskId;
    }

    private sealed record GuestSubmitResponse(string Id);

    private sealed record GuestTaskDto(string Id, string Url);
}
