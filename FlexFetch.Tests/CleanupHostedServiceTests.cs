using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.HostedServices;
using FlexFetch.Services;
using FlexFetch.Services.Tasks;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class CleanupHostedServiceTests
{
    private string? _dir;
    private LiteDbStore? _store;
    private IShareRepository? _shares;
    private ITaskRepository? _tasks;
    private TestConfig? _config;
    private IUserRepository? _users;
    private IGuestRepository? _guests;
    private StorageService? _storage;

    private static readonly ILogger Log = TestLog.Instance;

    private static readonly IHostApplicationLifetime Lifetime = new FakeLifetime();

    [TestInitialize]
    public void Setup()
    {
        _dir = TestApp.CreateTempDataDir();
        _store = new LiteDbStore(Path.Combine(_dir, "flexfetch.db"));
        _shares = new ShareRepository(_store);
        _tasks = new TaskRepository(_store);
        _config = new TestConfig();
        _users = new UserRepository(_store);
        _guests = new GuestRepository(_store);
        _storage = new StorageService(_dir);
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

    private CleanupHostedService CreateService(TaskService? taskService = null, UserService? userService = null)
    {
        var tasks = taskService ?? new TaskService(_tasks!, _shares!, _store!, _config!, new NoopExecutor(), _storage!, Log);
        var users = userService ?? new UserService(_users!, _config!);
        return new CleanupHostedService(
            _shares!, _tasks!, _config!, _users!, _guests!, tasks, users, _storage!, Lifetime, Log);
    }

    [TestMethod]
    public async Task Cleanup_DeletesExpiredSharesAndKeepsValidOnes()
    {
        _shares!.Insert(new ShareToken { TaskId = "t1", ExpiresAt = DateTime.UtcNow.AddHours(-1) });
        _shares.Insert(new ShareToken { TaskId = "t2", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        _shares.Insert(new ShareToken { TaskId = "t3" });

        using var taskService = new TaskService(_tasks!, _shares!, _store!, _config!, new NoopExecutor(), _storage!, Log);
        var service = CreateService(taskService);
        await service.ExecuteOnceForTestAsync(CancellationToken.None);

        Assert.IsNull(_shares.GetByToken(_shares.GetByTaskId("t1").SingleOrDefault()?.Token ?? ""));
        Assert.HasCount(1, _shares.GetByTaskId("t2"));
        Assert.HasCount(1, _shares.GetByTaskId("t3"));
    }

    [TestMethod]
    public async Task Cleanup_DeletesOrphanedTaskFiles()
    {
        // A task that exists keeps its files.
        var task = new TaskItem { OwnerUserId = "u", Url = "https://example.com/a.bin" };
        _tasks!.Insert(task);
        _storage!.EnsureTaskDir(task.Id);
        File.WriteAllText(_storage.GetTaskFilePath(task.Id, "a.bin"), "x");

        // A directory with no matching task is orphaned.
        var orphanId = "orphan123";
        _storage.EnsureTaskDir(orphanId);
        File.WriteAllText(_storage.GetTaskFilePath(orphanId, "b.bin"), "y");

        using var taskService = new TaskService(_tasks!, _shares!, _store!, _config!, new NoopExecutor(), _storage!, Log);
        var service = CreateService(taskService);
        await service.ExecuteOnceForTestAsync(CancellationToken.None);

        Assert.IsTrue(Directory.Exists(_storage.GetTaskDir(task.Id)));
        Assert.IsFalse(Directory.Exists(_storage.GetTaskDir(orphanId)));
    }

    [TestMethod]
    public async Task InactiveUserCleanup_RemovesResourcesKeepsAccount()
    {
        _config!.Set(ConfigKeys.InactiveDays, "30");
        var active = new User { UserName = "active", LastLoginAt = DateTime.UtcNow };
        var inactive = new User { UserName = "inactive", CreatedAt = DateTime.UtcNow.AddDays(-60) };
        _users!.Insert(active);
        _users.Insert(inactive);

        using var taskService = new TaskService(_tasks!, _shares!, _store!, _config, new NoopExecutor(), _storage!, Log);
        var activeTaskId = taskService.Submit(active.Id, "https://example.com/a.bin");
        var inactiveTaskId = taskService.Submit(inactive.Id, "https://example.com/b.bin");

        var service = CreateService(taskService);
        await service.ExecuteOnceForTestAsync(CancellationToken.None);

        // Inactive user's resources are gone; account remains.
        Assert.IsNull(_tasks!.GetById(inactiveTaskId));
        Assert.IsNotNull(_users.GetById(inactive.Id));

        // Active user untouched.
        Assert.IsNotNull(_tasks.GetById(activeTaskId));
    }

    [TestMethod]
    public async Task InactiveUserCleanup_DisabledWhenThresholdZero()
    {
        _config!.Set(ConfigKeys.InactiveDays, "0");
        var old = new User { UserName = "old", CreatedAt = DateTime.UtcNow.AddDays(-365) };
        _users!.Insert(old);

        using var taskService = new TaskService(_tasks!, _shares!, _store!, _config, new NoopExecutor(), _storage!, Log);
        var taskId = taskService.Submit(old.Id, "https://example.com/a.bin");

        var service = CreateService(taskService);
        await service.ExecuteOnceForTestAsync(CancellationToken.None);

        Assert.IsNotNull(_tasks!.GetById(taskId));
    }

    [TestMethod]
    public async Task ExpiredGuestSession_RemovesTasksFilesAndRecord()
    {
        _config!.Set(ConfigKeys.AnonymousSessionHours, "24");
        var guestId = "guest-testexpire";
        _guests!.Insert(new GuestSession
        {
            Id = guestId,
            CreatedAt = DateTime.UtcNow.AddHours(-48),
            LastActiveAt = DateTime.UtcNow.AddHours(-48),
        });

        // A finished download (inserted directly: Submit would leave the task
        // queued until a worker picks it up, and queued tasks delay cleanup).
        var taskId = FlexFetch.Entities.RandomId.New();
        _tasks!.Insert(new TaskItem
        {
            Id = taskId,
            OwnerUserId = guestId,
            Url = "https://example.com/a.bin",
            FileName = "a.bin",
            Status = Enums.TaskStatus.Completed,
            Progress = 100,
        });
        _storage!.EnsureTaskDir(taskId);
        File.WriteAllText(_storage.GetTaskFilePath(taskId, "a.bin"), "content");

        var service = CreateService();
        await service.ExecuteOnceForTestAsync(CancellationToken.None);

        Assert.IsNull(_tasks.GetById(taskId));
        Assert.IsFalse(Directory.Exists(_storage.GetTaskDir(taskId)));
        Assert.IsNull(_guests.GetById(guestId));
    }

    [TestMethod]
    public async Task ActiveGuestSession_Kept()
    {
        _config!.Set(ConfigKeys.AnonymousSessionHours, "24");
        var guestId = "guest-testactive";
        _guests!.Insert(new GuestSession { Id = guestId, LastActiveAt = DateTime.UtcNow });

        using var taskService = new TaskService(_tasks!, _shares!, _store!, _config, new NoopExecutor(), _storage!, Log);
        var taskId = taskService.Submit(guestId, "https://example.com/a.bin");
        _storage!.EnsureTaskDir(taskId);
        File.WriteAllText(_storage.GetTaskFilePath(taskId, "a.bin"), "content");

        var service = CreateService(taskService);
        await service.ExecuteOnceForTestAsync(CancellationToken.None);

        Assert.IsNotNull(_tasks!.GetById(taskId));
        Assert.IsTrue(Directory.Exists(_storage.GetTaskDir(taskId)));
        Assert.IsNotNull(_guests.GetById(guestId));
    }

    [TestMethod]
    public async Task GuestSessionWithRunningTask_KeptUntilFinished()
    {
        _config!.Set(ConfigKeys.AnonymousSessionHours, "24");
        var guestId = "guest-testrunning";
        _guests!.Insert(new GuestSession
        {
            Id = guestId,
            CreatedAt = DateTime.UtcNow.AddHours(-48),
            LastActiveAt = DateTime.UtcNow.AddHours(-48),
        });

        // A long-running download is never interrupted by the sweep; the
        // session goes once the task no longer runs.
        var running = new TaskItem
        {
            OwnerUserId = guestId,
            Url = "https://example.com/slow.bin",
            Status = Enums.TaskStatus.Running,
        };
        _tasks!.Insert(running);

        var service = CreateService();
        await service.ExecuteOnceForTestAsync(CancellationToken.None);

        Assert.IsNotNull(_tasks.GetById(running.Id));
        Assert.IsNotNull(_guests.GetById(guestId));
    }

    [TestMethod]
    public async Task GuestSessionCleanup_DisabledWhenThresholdZero()
    {
        _config!.Set(ConfigKeys.AnonymousSessionHours, "0");
        var guestId = "guest-testdisabled";
        _guests!.Insert(new GuestSession
        {
            Id = guestId,
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            LastActiveAt = DateTime.UtcNow.AddDays(-30),
        });

        using var taskService = new TaskService(_tasks!, _shares!, _store!, _config, new NoopExecutor(), _storage!, Log);
        var taskId = taskService.Submit(guestId, "https://example.com/a.bin");

        var service = CreateService(taskService);
        await service.ExecuteOnceForTestAsync(CancellationToken.None);

        Assert.IsNotNull(_tasks!.GetById(taskId));
        Assert.IsNotNull(_guests.GetById(guestId));
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class NoopExecutor : ITaskExecutor
    {
        public Task<TaskExecutionResult> ExecuteAsync(TaskItem task, Action<double> progress, CancellationToken cancellationToken)
        {
            progress(1.0);
            return Task.FromResult(TaskExecutionResult.Completed);
        }
    }
}
