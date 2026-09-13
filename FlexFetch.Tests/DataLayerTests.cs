using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Enums;
using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.Tests;

/// <summary>
/// Data layer tests: write, close, reopen the same database file and read back.
/// </summary>
[TestClass]
public sealed class DataLayerTests
{
    private string? _dir;

    [TestCleanup]
    public void Cleanup()
    {
        if (_dir is not null && Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string NewDbPath()
    {
        _dir = Path.Combine(Path.GetTempPath(), "flexfetch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, "flexfetch.db");
    }

    [TestMethod]
    public void UserRepository_RoundTripsAcrossReopen()
    {
        var path = NewDbPath();
        string userId;

        using (var store = new LiteDbStore(path))
        {
            var repo = new UserRepository(store);
            var user = new User
            {
                UserName = "alice",
                PasswordHash = "hash",
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                LastLoginAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            };
            repo.Insert(user);
            userId = user.Id;
        }

        using (var store = new LiteDbStore(path))
        {
            var repo = new UserRepository(store);
            var loaded = repo.GetById(userId);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("alice", loaded.UserName);
            Assert.AreEqual("hash", loaded.PasswordHash);
            Assert.AreEqual(UserRole.Admin, loaded.Role);
            Assert.AreEqual(UserStatus.Active, loaded.Status);
            Assert.AreEqual(userId, loaded.Id);

            var byName = repo.GetByUserName("alice");
            Assert.IsNotNull(byName);
            Assert.AreEqual(userId, byName.Id);
        }
    }

    [TestMethod]
    public void UserRepository_UpdateAndDelete()
    {
        var path = NewDbPath();

        using var store = new LiteDbStore(path);
        var repo = new UserRepository(store);
        var user = new User { UserName = "bob" };
        repo.Insert(user);

        user.Role = UserRole.Admin;
        Assert.IsTrue(repo.Update(user));
        Assert.AreEqual(UserRole.Admin, repo.GetById(user.Id)!.Role);

        Assert.IsTrue(repo.Delete(user.Id));
        Assert.IsNull(repo.GetById(user.Id));
    }

    [TestMethod]
    public void TaskRepository_RoundTripsAndFilters()
    {
        var path = NewDbPath();

        using var store = new LiteDbStore(path);
        var repo = new TaskRepository(store);
        var owner = "user-1";

        var task = new TaskItem
        {
            OwnerUserId = owner,
            Url = "https://example.com/video.mp4",
            FileName = "video.mp4",
            FileSize = 12345,
            Progress = 50,
            Status = TaskStatus.Running,
            DownloaderType = "Generic",
        };
        repo.Insert(task);

        var child = new TaskItem
        {
            OwnerUserId = owner,
            Url = "https://example.com/clip.mp4",
            ParentId = task.Id,
            Status = TaskStatus.Queued,
        };
        repo.Insert(child);

        Assert.IsNotNull(repo.GetById(task.Id));
        Assert.HasCount(2, repo.GetByOwner(owner));
        Assert.HasCount(1, repo.GetByStatus(TaskStatus.Running));
        Assert.HasCount(1, repo.GetChildren(task.Id));

        task.Status = TaskStatus.Completed;
        task.Progress = 100;
        Assert.IsTrue(repo.Update(task));
        Assert.AreEqual(TaskStatus.Completed, repo.GetById(task.Id)!.Status);
        Assert.HasCount(0, repo.GetByStatus(TaskStatus.Running));

        Assert.IsTrue(repo.Delete(child.Id));
        Assert.IsNull(repo.GetById(child.Id));
    }

    [TestMethod]
    public void ShareRepository_RoundTripsAndTokenLookup()
    {
        var path = NewDbPath();

        using var store = new LiteDbStore(path);
        var repo = new ShareRepository(store);
        var share = new ShareToken
        {
            TaskId = "task-1",
            ExpiresAt = DateTime.UtcNow.AddHours(24),
        };
        repo.Insert(share);

        var loaded = repo.GetByToken(share.Token);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("task-1", loaded.TaskId);
        Assert.AreEqual(share.Token, loaded.Token);

        Assert.HasCount(1, repo.GetByTaskId("task-1"));
        Assert.IsTrue(repo.DeleteByTaskId("task-1"));
        Assert.IsNull(repo.GetByToken(share.Token));
    }

    [TestMethod]
    public void ShareRepository_ExpiredFilter()
    {
        var path = NewDbPath();

        using var store = new LiteDbStore(path);
        var repo = new ShareRepository(store);
        repo.Insert(new ShareToken { TaskId = "t1", ExpiresAt = DateTime.UtcNow.AddHours(-1) });
        repo.Insert(new ShareToken { TaskId = "t2", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        repo.Insert(new ShareToken { TaskId = "t3" });

        Assert.HasCount(1, repo.GetExpired(DateTime.UtcNow));
    }

    [TestMethod]
    public async Task ConcurrentInserts_AreNotLost()
    {
        var path = NewDbPath();

        using var store = new LiteDbStore(path);
        var repo = new TaskRepository(store);
        const int count = 50;

        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(() =>
            {
                repo.Insert(new TaskItem { OwnerUserId = "u", Url = $"https://example.com/{i}" });
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.HasCount(count, repo.GetByOwner("u"));
    }
}
