using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FlexFetch.Tests;

[TestClass]
public sealed class TasksApiTests
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
    public async Task Tasks_RequireLogin()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/tasks");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task SubmitAndList_OwnershipIsolated()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        var alice = (await LoginAsync(factory, "alice")).Client;
        var bob = (await LoginAsync(factory, "bob")).Client;

        var submit = await alice.PostAsJsonAsync("/api/tasks", new { url = "https://example.com/a.bin" });
        Assert.AreEqual(HttpStatusCode.OK, submit.StatusCode);
        var created = await submit.Content.ReadFromJsonAsync<SubmitResponse>();
        Assert.IsNotNull(created);

        var aliceTasks = await alice.GetFromJsonAsync<List<TaskItemDto>>("/api/tasks");
        Assert.IsNotNull(aliceTasks);
        Assert.IsTrue(aliceTasks.Any(t => t.Id == created.Id));

        var bobTasks = await bob.GetFromJsonAsync<List<TaskItemDto>>("/api/tasks");
        Assert.IsNotNull(bobTasks);
        Assert.IsFalse(bobTasks.Any(t => t.Id == created.Id));
    }

    [TestMethod]
    public async Task Submit_RejectsNonHttpUrls()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        using var client = (await LoginAsync(factory, "alice")).Client;

        var ftp = await client.PostAsJsonAsync("/api/tasks", new { url = "ftp://example.com/a.bin" });
        Assert.AreEqual(HttpStatusCode.BadRequest, ftp.StatusCode);

        var garbage = await client.PostAsJsonAsync("/api/tasks", new { url = "not-a-url" });
        Assert.AreEqual(HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    [TestMethod]
    public async Task Delete_OnlyByOwner()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        var alice = (await LoginAsync(factory, "alice")).Client;
        var bob = (await LoginAsync(factory, "bob")).Client;

        var submit = await alice.PostAsJsonAsync("/api/tasks", new { url = "https://example.com/a.bin" });
        var created = await submit.Content.ReadFromJsonAsync<SubmitResponse>();
        Assert.IsNotNull(created);

        // Bob cannot delete Alice's task.
        var foreign = await bob.DeleteAsync($"/api/tasks/{created.Id}");
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);

        // Alice can.
        var own = await alice.DeleteAsync($"/api/tasks/{created.Id}");
        Assert.AreEqual(HttpStatusCode.OK, own.StatusCode);
    }

    [TestMethod]
    public async Task Share_GeneratesTokenAndReadOnlyAccess()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        var (alice, aliceId) = await LoginAsync(factory, "alice");

        // A completed task with a file can be shared.
        var taskId = SeedCompletedTask(factory, aliceId, "report.pdf", "PDF-CONTENT");
        var share = await alice.PostAsync($"/api/tasks/{taskId}/share", null);
        Assert.AreEqual(HttpStatusCode.OK, share.StatusCode);
        var shareBody = await share.Content.ReadFromJsonAsync<ShareResponse>();
        Assert.IsNotNull(shareBody);

        // Anonymous visitor can view and download.
        using var visitor = factory.CreateClient();
        var view = await visitor.GetAsync($"/api/share/{shareBody.Token}");
        Assert.AreEqual(HttpStatusCode.OK, view.StatusCode);
        var viewBody = await view.Content.ReadFromJsonAsync<ShareViewResponse>();
        Assert.IsNotNull(viewBody);
        Assert.AreEqual("report.pdf", viewBody.FileName);

        var file = await visitor.GetAsync($"/api/share/{shareBody.Token}/file");
        Assert.AreEqual(HttpStatusCode.OK, file.StatusCode);
        var content = await file.Content.ReadAsStringAsync();
        Assert.AreEqual("PDF-CONTENT", content);
        Assert.IsTrue(file.Content.Headers.ContentDisposition?.FileNameStar?.Contains("report.pdf") == true
            || file.Content.Headers.ContentDisposition?.FileName?.Contains("report.pdf") == true);
    }

    [TestMethod]
    public async Task Share_FileGoneAfterTaskDeleted()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        var (alice, aliceId) = await LoginAsync(factory, "alice");
        var taskId = SeedCompletedTask(factory, aliceId, "report.pdf", "PDF-CONTENT");

        var share = await alice.PostAsync($"/api/tasks/{taskId}/share", null);
        var shareBody = await share.Content.ReadFromJsonAsync<ShareResponse>();
        Assert.IsNotNull(shareBody);

        await alice.DeleteAsync($"/api/tasks/{taskId}");

        using var visitor = factory.CreateClient();
        var view = await visitor.GetAsync($"/api/share/{shareBody.Token}");
        Assert.AreEqual(HttpStatusCode.NotFound, view.StatusCode);
        var file = await visitor.GetAsync($"/api/share/{shareBody.Token}/file");
        Assert.AreEqual(HttpStatusCode.NotFound, file.StatusCode);
    }

    [TestMethod]
    public async Task Share_UnfinishedTask_CannotDownloadFile()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        var alice = (await LoginAsync(factory, "alice")).Client;

        var submit = await alice.PostAsJsonAsync("/api/tasks", new { url = "https://example.com/slow.bin" });
        var created = await submit.Content.ReadFromJsonAsync<SubmitResponse>();
        Assert.IsNotNull(created);

        var share = await alice.PostAsync($"/api/tasks/{created.Id}/share", null);
        var shareBody = await share.Content.ReadFromJsonAsync<ShareResponse>();
        Assert.IsNotNull(shareBody);

        using var visitor = factory.CreateClient();
        var file = await visitor.GetAsync($"/api/share/{shareBody.Token}/file");

        Assert.AreEqual(HttpStatusCode.Conflict, file.StatusCode);
    }

    [TestMethod]
    public async Task Retry_OnlyByOwner_RejectsRunning()
    {
        using var factory = TestApp.CreateFactory(_dataDir!);
        var alice = (await LoginAsync(factory, "alice")).Client;
        var bob = (await LoginAsync(factory, "bob")).Client;

        var submit = await alice.PostAsJsonAsync("/api/tasks", new { url = "https://example.com/a.bin" });
        var created = await submit.Content.ReadFromJsonAsync<SubmitResponse>();
        Assert.IsNotNull(created);

        var foreign = await bob.PostAsync($"/api/tasks/{created.Id}/retry", null);
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private async Task<(HttpClient Client, string UserId)> LoginAsync(WebApplicationFactory<Program> factory, string userName)
    {
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new { userName, password = "password1" });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = "password1" });
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<LoginResponse>();
        return (client, body!.Id);
    }

    private string SeedCompletedTask(WebApplicationFactory<Program> factory, string ownerUserId, string fileName, string content)
    {
        var taskId = FlexFetch.Domain.RandomId.New();
        var filesDir = Path.Combine(_dataDir!, "files", taskId);
        Directory.CreateDirectory(filesDir);
        File.WriteAllText(Path.Combine(filesDir, fileName), content);

        // Persist a completed task owned by the given user so the share endpoints resolve it.
        using var scope = factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<FlexFetch.Data.ITaskRepository>();
        tasks.Insert(new FlexFetch.Domain.TaskItem
        {
            Id = taskId,
            OwnerUserId = ownerUserId,
            Url = "https://example.com/report.pdf",
            FileName = fileName,
            Status = FlexFetch.Domain.TaskStatus.Completed,
            Progress = 100,
        });
        return taskId;
    }

    private sealed record SubmitResponse(string Id);

    private sealed record ShareResponse(string Token, DateTime? ExpiresAt);

    private sealed record ShareViewResponse(string? FileName, long? FileSize, string Status, double Progress, string? ErrorMessage);

    private sealed record TaskItemDto(string Id, string Url, int Status);

    private sealed record LoginResponse(string Id, string UserName, string Role);
}
