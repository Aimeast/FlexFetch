using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Domain;
using FlexFetch.Services;
using Serilog;
using TaskStatus = FlexFetch.Domain.TaskStatus;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class TaskServiceTests
{
    private string? _dir;
    private LiteDbStore? _store;
    private TaskService? _service;
    private ITaskRepository? _tasks;
    private IConfigRepository? _config;
    private FakeExecutor? _executor;
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
        _tasks = new TaskRepository(_store);
        var shares = new ShareRepository(_store);
        _config = new ConfigRepository(_store);
        _storage = new StorageService(_dir);
        _executor = new FakeExecutor();
        _service = new TaskService(_tasks, shares, _config, _executor, _storage, Log);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _service?.Dispose();
        _store?.Dispose();
        if (_dir is not null && Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Submit_CompletesTask()
    {
        _executor!.Handler = (task, progress, ct) =>
        {
            progress(0.5);
            task.FileName = "file.bin";
            return Task.CompletedTask;
        };

        var id = _service!.Submit("user-1", "https://example.com/file.bin");

        await WaitForStatusAsync(id, TaskStatus.Completed);
        var task = _service.GetById(id);
        Assert.AreEqual(TaskStatus.Completed, task!.Status);
        Assert.AreEqual(100, task.Progress);
        Assert.AreEqual("file.bin", task.FileName);
        Assert.IsNotNull(task.CompletedAt);
    }

    [TestMethod]
    public async Task Submit_FailedTask_RetriesUpToLimitThenFails()
    {
        _config!.Set(ConfigKeys.MaxRetries, "2");
        _executor!.Handler = (task, progress, ct) => throw new InvalidOperationException("boom");

        var id = _service!.Submit("user-1", "https://example.com/bad.bin");

        await WaitForStatusAsync(id, TaskStatus.Failed);
        var task = _service.GetById(id);
        Assert.AreEqual(TaskStatus.Failed, task!.Status);
        Assert.IsTrue(task.Attempts >= 3, $"Expected at least 3 attempts, got {task.Attempts}");
        Assert.AreEqual("boom", task.ErrorMessage);
    }

    [TestMethod]
    public async Task ConcurrencyLimit_IsEnforced()
    {
        _config!.Set(ConfigKeys.MaxConcurrency, "2");
        var active = 0;
        var maxActive = 0;
        var gate = new object();

        _executor!.Handler = async (task, progress, ct) =>
        {
            lock (gate)
            {
                active++;
                maxActive = Math.Max(maxActive, active);
            }

            await Task.Delay(200, ct);

            lock (gate)
            {
                active--;
            }
        };

        var ids = Enumerable.Range(0, 6)
            .Select(i => _service!.Submit("user-1", $"https://example.com/{i}.bin"))
            .ToList();

        await Task.WhenAll(ids.Select(id => WaitForStatusAsync(id, TaskStatus.Completed)));

        Assert.IsLessThanOrEqualTo(maxActive, 2);
    }

    [TestMethod]
    public async Task Cancel_RunningTask()
    {
        var started = new TaskCompletionSource();
        _executor!.Handler = async (task, progress, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var id = _service!.Submit("user-1", "https://example.com/slow.bin");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Cancel via the service's internal state is not exposed; instead
        // verify cancellation propagation through the executor's token by
        // deleting the task (which cancels running work).
        Assert.IsTrue(_service!.Delete(id));
        await Task.Delay(100);

        var task = _service.GetById(id);
        Assert.IsNull(task);
    }

    [TestMethod]
    public async Task RecoverPending_RequeuesRunningAndQueued()
    {
        // Simulate a restart: a task stuck in Running.
        var orphan = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/orphan.bin", Status = TaskStatus.Running };
        _tasks!.Insert(orphan);

        _executor!.Handler = (task, progress, ct) =>
        {
            task.FileName = "recovered.bin";
            return Task.CompletedTask;
        };

        _service!.RecoverPending();

        await WaitForStatusAsync(orphan.Id, TaskStatus.Completed);
        var task = _service.GetById(orphan.Id);
        Assert.AreEqual(TaskStatus.Completed, task!.Status);
        Assert.AreEqual("recovered.bin", task.FileName);
    }

    [TestMethod]
    public void AggregateParent_SummarizesChildren()
    {
        var parent = new TaskItem
        {
            OwnerUserId = "user-1",
            Url = "https://example.com/playlist",
            DownloaderType = "Playlist",
            Status = TaskStatus.Running,
            IsVirtual = true,
        };
        _tasks!.Insert(parent);

        _tasks.Insert(new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/a.mp4", ParentId = parent.Id, Status = TaskStatus.Completed, Progress = 100 });
        _tasks.Insert(new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/b.mp4", ParentId = parent.Id, Status = TaskStatus.Failed, ErrorMessage = "oops" });
        _tasks.Insert(new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/c.mp4", ParentId = parent.Id, Status = TaskStatus.Queued, Progress = 0 });

        _service!.AggregateParent(parent.Id);

        var loaded = _tasks.GetById(parent.Id)!;
        Assert.AreEqual(TaskStatus.Failed, loaded.Status);
        StringAssert.Contains(loaded.ErrorMessage, "oops");
    }

    [TestMethod]
    public async Task Retry_FailedTask()
    {
        var failFirst = true;
        _executor!.Handler = (task, progress, ct) =>
        {
            if (failFirst)
            {
                failFirst = false;
                throw new InvalidOperationException("first");
            }

            task.FileName = "ok.bin";
            return Task.CompletedTask;
        };

        // Force a failure by using retries=0, then retry manually.
        _config!.Set(ConfigKeys.MaxRetries, "0");
        var id = _service!.Submit("user-1", "https://example.com/retry.bin");
        await WaitForStatusAsync(id, TaskStatus.Failed);
        Assert.AreEqual("first", _service.GetById(id)!.ErrorMessage);

        Assert.IsTrue(_service!.Retry(id));
        await WaitForStatusAsync(id, TaskStatus.Completed);
        Assert.AreEqual("ok.bin", _service.GetById(id)!.FileName);

        // Retrying a completed task is rejected.
        Assert.IsFalse(_service.Retry(id));
    }

    [TestMethod]
    public async Task Delete_RemovesTaskAndFiles()
    {
        _executor!.Handler = (task, progress, ct) =>
        {
            _storage!.EnsureTaskDir(task.Id);
            File.WriteAllText(_storage.GetTaskFilePath(task.Id, "file.bin"), "data");
            task.FileName = "file.bin";
            return Task.CompletedTask;
        };

        var id = _service!.Submit("user-1", "https://example.com/del.bin");
        await WaitForStatusAsync(id, TaskStatus.Completed);

        Assert.IsTrue(File.Exists(_storage!.GetTaskFilePath(id, "file.bin")));
        Assert.IsTrue(_service.Delete(id));
        Assert.IsNull(_service.GetById(id));
        Assert.IsFalse(Directory.Exists(_storage.GetTaskDir(id)));

        // Deleting a missing task is a no-op.
        Assert.IsFalse(_service.Delete("missing"));
    }

    private async Task WaitForStatusAsync(string id, TaskStatus status)
    {
        for (var i = 0; i < 100; i++)
        {
            var task = _service!.GetById(id);
            if (task is not null && task.Status == status)
            {
                return;
            }

            if (task is not null && status == TaskStatus.Completed && task.Status == TaskStatus.Failed)
            {
                throw new AssertFailedException($"Task failed unexpectedly: {task.ErrorMessage}");
            }

            await Task.Delay(50);
        }

        throw new AssertFailedException($"Timed out waiting for status {status} of task {id}");
    }

    private sealed class FakeExecutor : ITaskExecutor
    {
        public Func<TaskItem, Action<double>, CancellationToken, Task> Handler { get; set; } =
            (_, _, _) => Task.CompletedTask;

        public Task ExecuteAsync(TaskItem task, Action<double> progress, CancellationToken cancellationToken) =>
            Handler(task, progress, cancellationToken);
    }
}
