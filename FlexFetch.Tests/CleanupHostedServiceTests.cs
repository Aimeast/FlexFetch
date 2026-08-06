using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Domain;
using FlexFetch.HostedServices;
using FlexFetch.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class CleanupHostedServiceTests
{
    private string? _dir;
    private LiteDbStore? _store;
    private IShareRepository? _shares;
    private ITaskRepository? _tasks;
    private IConfigRepository? _config;
    private IUserRepository? _users;
    private StorageService? _storage;

    private static readonly ILogger Log = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "flexfetch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new LiteDbStore(Path.Combine(_dir, "flexfetch.db"));
        _shares = new ShareRepository(_store);
        _tasks = new TaskRepository(_store);
        _config = new ConfigRepository(_store);
        _users = new UserRepository(_store);
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

    [TestMethod]
    public async Task Cleanup_DeletesExpiredSharesAndKeepsValidOnes()
    {
        _shares!.Insert(new ShareToken { TaskId = "t1", ExpiresAt = DateTime.UtcNow.AddHours(-1) });
        _shares.Insert(new ShareToken { TaskId = "t2", ExpiresAt = DateTime.UtcNow.AddHours(1) });
        _shares.Insert(new ShareToken { TaskId = "t3" });

        var service = new CleanupHostedService(_shares, _tasks!, _storage!, Log);
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

        var service = new CleanupHostedService(_shares!, _tasks, _storage, Log);
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

        var taskService = new TaskService(_tasks!, _shares!, _config, new NoopExecutor(), _storage!, Log);
        var activeTaskId = taskService.Submit(active.Id, "https://example.com/a.bin");
        var inactiveTaskId = taskService.Submit(inactive.Id, "https://example.com/b.bin");

        var service = new InactiveUserCleanupHostedService(_config, _users, taskService, new UserService(_users, _config), Log);
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

        var taskService = new TaskService(_tasks!, _shares!, _config, new NoopExecutor(), _storage!, Log);
        var taskId = taskService.Submit(old.Id, "https://example.com/a.bin");

        var service = new InactiveUserCleanupHostedService(_config, _users, taskService, new UserService(_users, _config), Log);
        await service.ExecuteOnceForTestAsync(CancellationToken.None);

        Assert.IsNotNull(_tasks!.GetById(taskId));
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
