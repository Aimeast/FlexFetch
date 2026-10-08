using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Tasks;
using ILogger = Serilog.ILogger;
using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.Tests;

[TestClass]
public sealed class TaskServiceTests
{
    private string? _dir;
    private LiteDbStore? _store;
    private TaskService? _service;
    private ITaskRepository? _tasks;
    private TestConfig? _config;
    private FakeExecutor? _executor;
    private StorageService? _storage;

    private static readonly ILogger Log = TestLog.Instance;

    [TestInitialize]
    public void Setup()
    {
        _dir = TestApp.CreateTempDataDir();
        _store = new LiteDbStore(Path.Combine(_dir, "flexfetch.db"));
        _tasks = new TaskRepository(_store);
        var shares = new ShareRepository(_store);
        _config = new TestConfig();
        _storage = new StorageService(_dir);
        _executor = new FakeExecutor();
        _service = new TaskService(_tasks, shares, _store, _config, _executor, _storage, Log);
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
    public async Task Submit_StoresProgressOnHundredScale()
    {
        // Downloaders report a 0..1 fraction; the store must carry 0..100
        // (the UI divides by 100 and the completion path writes 100).
        var reported = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        _executor!.Handler = (task, progress, ct) =>
        {
            progress(0.5);
            reported.TrySetResult();
            return release.Task;
        };

        var id = _service!.Submit("user-1", "https://example.com/file.bin");

        await reported.Task;
        Assert.AreEqual(50, _service.GetById(id)!.Progress);

        release.SetResult();
        await WaitForStatusAsync(id, TaskStatus.Completed);
        Assert.AreEqual(100, _service.GetById(id)!.Progress);
    }

    [TestMethod]
    public async Task Submit_FailedTask_RetriesUpToLimitThenFails()
    {
        _config!.Set(ConfigKeys.MaxRetries, "2");
        _executor!.Handler = (task, progress, ct) => throw new RetryableException("boom");

        var id = _service!.Submit("user-1", "https://example.com/bad.bin");

        await WaitForStatusAsync(id, TaskStatus.Failed);
        var task = _service.GetById(id);
        Assert.AreEqual(TaskStatus.Failed, task!.Status);
        Assert.IsGreaterThanOrEqualTo(3, task.Attempts);
        Assert.AreEqual("boom", task.ErrorMessage);
    }

    [TestMethod]
    public async Task Submit_DeterministicError_FailsImmediatelyWithoutRetry()
    {
        // Only transient (RetryableException) errors are retried; a plain
        // failure fails the task right away with a single attempt.
        var calls = 0;
        _executor!.Handler = (task, progress, ct) =>
        {
            calls++;
            throw new InvalidOperationException("boom");
        };

        var id = _service!.Submit("user-1", "https://example.com/bad.bin");

        await WaitForStatusAsync(id, TaskStatus.Failed);
        var task = _service.GetById(id);
        Assert.AreEqual(TaskStatus.Failed, task!.Status);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, task.Attempts);
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
        // Simulate a restart: tasks stuck in Running and Queued must be
        // re-queued; Failed (a real download failure) is not re-queued.
        var running = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/r.bin", Status = TaskStatus.Running };
        var queued = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/q.bin", Status = TaskStatus.Queued };
        var failed = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/f.bin", Status = TaskStatus.Failed };
        _tasks!.Insert(running);
        _tasks.Insert(queued);
        _tasks.Insert(failed);

        _executor!.Handler = (task, progress, ct) =>
        {
            task.FileName = "recovered.bin";
            return Task.CompletedTask;
        };

        _service!.RecoverPending();

        await WaitForStatusAsync(running.Id, TaskStatus.Completed);
        await WaitForStatusAsync(queued.Id, TaskStatus.Completed);
        Assert.AreEqual("recovered.bin", _service!.GetById(running.Id)!.FileName);
        Assert.AreEqual("recovered.bin", _service!.GetById(queued.Id)!.FileName);

        // Failed stays failed - it was a real download failure.
        Assert.AreEqual(TaskStatus.Failed, _service.GetById(failed.Id)!.Status);
    }

    [TestMethod]
    public async Task RecoverPending_ClearsGenericYtdlpPin()
    {
        // "Ytdlp" is the generic fallback a previous attempt happened to
        // pick, never an intentional pin: recovery must clear it so the URL
        // is re-selected (a newer downloader may claim it now), while real
        // pins survive.
        var fallback = new TaskItem
        {
            OwnerUserId = "user-1",
            Url = "https://example.com/media/md28229591",
            DownloaderType = "Ytdlp",
            Status = TaskStatus.Running,
        };
        var pinned = new TaskItem
        {
            OwnerUserId = "user-1",
            Url = "https://cdn.example.com/a.mp4",
            DownloaderType = "Generic",
            Status = TaskStatus.Running,
        };
        _tasks!.Insert(fallback);
        _tasks.Insert(pinned);
        _executor!.Handler = (_, _, _) => Task.CompletedTask;

        _service!.RecoverPending();

        await WaitForStatusAsync(fallback.Id, TaskStatus.Completed);
        await WaitForStatusAsync(pinned.Id, TaskStatus.Completed);

        Assert.IsNull(_tasks.GetById(fallback.Id)!.DownloaderType);
        Assert.AreEqual("Generic", _tasks.GetById(pinned.Id)!.DownloaderType);
    }

    [TestMethod]
    public async Task Execute_SkipsTaskDeletedWhileQueued()
    {
        // Deleting a parent leaves its queued children in the in-memory
        // channel (a channel cannot drop individual items). A dequeued child
        // whose record is gone must be skipped, not re-downloaded as an
        // invisible zombie.
        _service!.Dispose();
        _config!.Set(ConfigKeys.MaxConcurrency, "1");
        var shares = new ShareRepository(_store!);
        _service = new TaskService(_tasks!, shares, _store!, _config, _executor!, _storage!, Log);

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var zombieRan = false;
        _executor!.Handler = (task, _, _) =>
        {
            if (task.Url.Contains("first", StringComparison.Ordinal))
            {
                started.TrySetResult();
                return release.Task;
            }

            zombieRan = true;
            return Task.CompletedTask;
        };

        _ = _service.Submit("user-1", "https://example.com/first.bin");
        await started.Task; // the first task runs, holding the single slot
        var zombieId = _service.Submit("user-1", "https://example.com/zombie.bin");

        // The record is gone while the item still sits in the channel.
        Assert.IsTrue(_service.Delete(zombieId));
        Assert.IsNull(_tasks!.GetById(zombieId));

        release.TrySetResult();
        foreach (var _ in Enumerable.Range(0, 100))
        {
            if (_service.RunningCount == 0 && _service.QueuedCount == 0)
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.IsFalse(zombieRan, "a deleted queued task must not be re-downloaded");
        Assert.IsFalse(Directory.Exists(_storage!.GetTaskDir(zombieId)));
    }

    [TestMethod]
    public void Delete_ParentCascadeRemovesChildrenAndShares()
    {
        var parent = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/list", Status = TaskStatus.Running, IsVirtual = true };
        var childA = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/a.mp4", ParentId = parent.Id, Status = TaskStatus.Completed };
        var childB = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/b.mp4", ParentId = parent.Id, Status = TaskStatus.Queued };
        _tasks!.Insert(parent);
        _tasks.Insert(childA);
        _tasks.Insert(childB);
        var shares = new ShareRepository(_store!);
        shares.Insert(new ShareToken { TaskId = childA.Id });

        Assert.IsTrue(_service!.Delete(parent.Id));

        Assert.IsNull(_tasks.GetById(parent.Id));
        Assert.IsNull(_tasks.GetById(childA.Id));
        Assert.IsNull(_tasks.GetById(childB.Id));
        Assert.IsEmpty(shares.GetByTaskId(childA.Id));
    }

    [TestMethod]
    public async Task Delete_CleansUpFilesInBackground()
    {
        // The API answers after the record commit; file removal follows in
        // the background (process kills and Defender-slow deletes must never
        // block the request).
        var task = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/v.mp4", Status = TaskStatus.Completed };
        _tasks!.Insert(task);
        var dir = _storage!.GetTaskDir(task.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "clip.mp4"), "abc");

        Assert.IsTrue(_service!.Delete(task.Id));
        Assert.IsNull(_tasks.GetById(task.Id));

        foreach (var _ in Enumerable.Range(0, 50))
        {
            if (!Directory.Exists(dir))
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.IsFalse(Directory.Exists(dir), "the background cleanup must remove the task directory");
    }

    [TestMethod]
    public void ReconcileFileNames_FixesFileSizeFromDisk()
    {
        // In the group layout the recorded name IS the disk name inside the
        // group folder; reconciliation only adopts the on-disk size when the
        // record lost it (a download-report encoding mismatch), and leaves
        // records without a file untouched.
        var sized = new TaskItem
        {
            OwnerUserId = "user-1",
            Url = "https://example.com/v.bin",
            StorageFolder = "sized",
            FileName = "clip.bin",
            FileSize = 999,
            Status = TaskStatus.Completed,
        };
        var noFile = new TaskItem
        {
            OwnerUserId = "user-1",
            Url = "https://example.com/w.bin",
            StorageFolder = "nofile",
            FileName = "gone.bin",
            Status = TaskStatus.Completed,
        };
        _tasks!.Insert(sized);
        _tasks.Insert(noFile);
        var dir = _storage!.GetTaskDir(sized);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "clip.bin"), "abc");

        var fixedCount = _service!.ReconcileFileNames();

        Assert.AreEqual(1, fixedCount);
        Assert.AreEqual(3, _tasks.GetById(sized.Id)!.FileSize);
        // A record without a matching file is left untouched.
        Assert.IsNull(_tasks.GetById(noFile.Id)!.FileSize);
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
    public async Task Retry_VirtualParent_RequeuesFailedChildrenInsteadOfReexpanding()
    {
        // A playlist parent already expanded into children (IsVirtual): retry
        // must re-queue the failed children, not re-run the parent (which
        // would create a second set of child tasks).
        var parentCalls = 0;
        var childCalls = 0;
        _executor!.Handler = (task, progress, ct) =>
        {
            if (task.IsVirtual)
            {
                parentCalls++;
                return Task.CompletedTask;
            }

            childCalls++;
            task.FileName = "child.bin";
            return Task.CompletedTask;
        };

        var parent = new TaskItem
        {
            OwnerUserId = "user-1",
            Url = "https://example.com/playlist",
            DownloaderType = "Playlist",
            Status = TaskStatus.Failed,
            IsVirtual = true,
            ErrorMessage = "#child-x: boom",
        };
        _tasks!.Insert(parent);
        var failedChild = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/a.mp4", ParentId = parent.Id, Status = TaskStatus.Failed, ErrorMessage = "boom" };
        var doneChild = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/b.mp4", ParentId = parent.Id, Status = TaskStatus.Completed };
        _tasks.Insert(failedChild);
        _tasks.Insert(doneChild);

        Assert.IsTrue(_service!.Retry(parent.Id));
        await WaitForStatusAsync(failedChild.Id, TaskStatus.Completed);
        await WaitForStatusAsync(doneChild.Id, TaskStatus.Completed);

        // The parent itself is never re-executed; only its failed child ran.
        Assert.AreEqual(0, parentCalls);
        Assert.AreEqual(1, childCalls);
        var reloadedParent = _service.GetById(parent.Id)!;
        Assert.IsTrue(reloadedParent.IsVirtual);
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

        // File removal runs in the background cleanup; give it a moment.
        foreach (var _ in Enumerable.Range(0, 50))
        {
            if (!Directory.Exists(_storage.GetTaskDir(id)))
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.IsFalse(Directory.Exists(_storage.GetTaskDir(id)));

        // Deleting a missing task is a no-op.
        Assert.IsFalse(_service.Delete("missing"));
    }

    [TestMethod]
    public async Task RecoverPending_SkipsTasksStillQueuedInChannel()
    {
        // Recovery can run long after startup (component installs delay it):
        // tasks submitted in the meantime already wait in the channel, and a
        // task holding the slot is executing. They are not orphans - the old
        // behavior re-queued them and every download ran twice.
        _service!.Dispose();
        _config!.Set(ConfigKeys.MaxConcurrency, "1");
        var shares = new ShareRepository(_store!);
        _service = new TaskService(_tasks!, shares, _store!, _config, _executor!, _storage!, Log);

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var secondDone = new TaskCompletionSource();
        var runs = new List<string>();
        var gate = new object();
        _executor!.Handler = (task, _, _) =>
        {
            lock (gate)
            {
                runs.Add(task.Url);
            }

            if (task.Url.Contains("first", StringComparison.Ordinal))
            {
                started.TrySetResult();
                return release.Task;
            }

            secondDone.TrySetResult();
            return Task.CompletedTask;
        };

        _ = _service.Submit("user-1", "https://example.com/first.bin");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // holds the single slot
        _ = _service.Submit("user-1", "https://example.com/second.bin");

        // Late recovery sees both records (first Running, second Queued).
        Assert.AreEqual(0, _service.RecoverPending());

        release.TrySetResult();
        await secondDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await DrainAsync();

        Assert.AreEqual(2, runs.Count, "each task must run exactly once");
    }

    [TestMethod]
    public async Task Execute_SkipsDuplicateEntryOfCompletedTask()
    {
        // A duplicate channel entry for a task that already finished must not
        // re-download: its stale in-memory copy would rewrite the record over
        // the fresh Completed state (the stuck-Running symptom).
        _service!.Dispose();
        _config!.Set(ConfigKeys.MaxConcurrency, "1");
        var shares = new ShareRepository(_store!);
        _service = new TaskService(_tasks!, shares, _store!, _config, _executor!, _storage!, Log);

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var duplicateRan = false;
        _executor!.Handler = (task, _, _) =>
        {
            if (task.Url.Contains("first", StringComparison.Ordinal))
            {
                started.TrySetResult();
                return release.Task;
            }

            duplicateRan = true;
            return Task.CompletedTask;
        };

        _ = _service.Submit("user-1", "https://example.com/first.bin");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicateId = _service.Submit("user-1", "https://example.com/duplicate.bin");

        // Simulate an earlier duplicate entry having already finished it.
        var record = _tasks!.GetById(duplicateId)!;
        record.Status = TaskStatus.Completed;
        record.Progress = 100;
        _tasks.Update(record);

        release.TrySetResult();
        await DrainAsync();

        Assert.IsFalse(duplicateRan, "a queue entry of a completed task must be skipped");
        Assert.AreEqual(TaskStatus.Completed, _service.GetById(duplicateId)!.Status);
        Assert.AreEqual(100, _service.GetById(duplicateId)!.Progress);
    }

    [TestMethod]
    public async Task Retry_TaskStillQueuedInChannel_DoesNotRunTwice()
    {
        // Retrying a task that still sits queued in the channel must accept
        // the request without creating a second queue entry (the stale-copy
        // duplication behind the "stuck running/queued" symptom).
        _service!.Dispose();
        _config!.Set(ConfigKeys.MaxConcurrency, "1");
        var shares = new ShareRepository(_store!);
        _service = new TaskService(_tasks!, shares, _store!, _config, _executor!, _storage!, Log);

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var secondDone = new TaskCompletionSource();
        var runs = new List<string>();
        var gate = new object();
        _executor!.Handler = (task, _, _) =>
        {
            lock (gate)
            {
                runs.Add(task.Url);
            }

            if (task.Url.Contains("first", StringComparison.Ordinal))
            {
                started.TrySetResult();
                return release.Task;
            }

            secondDone.TrySetResult();
            return Task.CompletedTask;
        };

        _ = _service.Submit("user-1", "https://example.com/first.bin");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondId = _service.Submit("user-1", "https://example.com/second.bin");

        Assert.IsTrue(_service.Retry(secondId));

        release.TrySetResult();
        await secondDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await DrainAsync();

        Assert.AreEqual(1, runs.Count(url => url.Contains("second", StringComparison.Ordinal)),
            "a queued task must not gain a second queue entry through retry");
    }

    private async Task DrainAsync()
    {
        foreach (var _ in Enumerable.Range(0, 100))
        {
            if (_service!.RunningCount == 0 && _service.QueuedCount == 0)
            {
                break;
            }

            await Task.Delay(50);
        }
    }

    private async Task WaitForStatusAsync(string id, TaskStatus status)
    {
        // Allow enough time for retry backoff (2s + 4s for a 3-attempt run).
        for (var i = 0; i < 400; i++)
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

    [TestMethod]
    public async Task Submit_MultipleTasks_ExecuteInSubmissionOrder()
    {
        // Multiple links (and playlist children) must START in submission
        // order: the start order is recorded, not the completion order
        // (a shorter later video may finish earlier under concurrency).
        var order = new List<string>();
        var gate = new object();
        _executor!.Handler = (task, progress, ct) =>
        {
            lock (gate)
            {
                order.Add(task.Url);
            }
            return Task.CompletedTask;
        };

        var urls = Enumerable.Range(0, 5).Select(i => $"https://example.com/{i}.bin").ToList();
        var ids = urls.Select(u => _service!.Submit("user-1", u)).ToList();

        await Task.WhenAll(ids.Select(id => WaitForStatusAsync(id, TaskStatus.Completed)));

        CollectionAssert.AreEqual(urls, order);
    }

    [TestMethod]
    public async Task RecoverPending_RequeuesInSubmissionOrder()
    {
        // A restart must not shuffle the order: merged Running+Queued orphans
        // are re-enqueued oldest first, even when stored in shuffled order.
        var now = DateTime.UtcNow;
        var tasks = Enumerable.Range(0, 5)
            .Select(i => new TaskItem
            {
                OwnerUserId = "user-1",
                Url = $"https://example.com/{i}.bin",
                Status = i % 2 == 0 ? TaskStatus.Running : TaskStatus.Queued,
                CreatedAt = now.AddSeconds(i),
            })
            .ToList();
        foreach (var task in tasks.AsEnumerable().Reverse())
        {
            _tasks!.Insert(task);
        }

        var order = new List<string>();
        var gate = new object();
        _executor!.Handler = (task, progress, ct) =>
        {
            lock (gate)
            {
                order.Add(task.Url);
            }
            return Task.CompletedTask;
        };

        _service!.RecoverPending();

        await Task.WhenAll(tasks.Select(t => WaitForStatusAsync(t.Id, TaskStatus.Completed)));

        CollectionAssert.AreEqual(tasks.Select(t => t.Url).ToList(), order);
    }

    [TestMethod]
    public async Task Retry_VirtualParent_RequeuesChildrenInPlaylistOrder()
    {
        // Retrying a playlist parent re-queues its failed children front to
        // back (creation order = playlist position), regardless of the
        // order the children happen to sit in the store.
        var parent = new TaskItem
        {
            OwnerUserId = "user-1",
            Url = "https://example.com/playlist",
            IsVirtual = true,
            Status = TaskStatus.Failed,
        };
        _tasks!.Insert(parent);

        var now = DateTime.UtcNow;
        var children = Enumerable.Range(0, 4)
            .Select(i => new TaskItem
            {
                OwnerUserId = "user-1",
                Url = $"https://example.com/video-{i}",
                ParentId = parent.Id,
                Status = TaskStatus.Failed,
                CreatedAt = now.AddSeconds(i),
            })
            .ToList();
        foreach (var child in children.AsEnumerable().Reverse())
        {
            _tasks!.Insert(child);
        }

        var order = new List<string>();
        var gate = new object();
        _executor!.Handler = (task, progress, ct) =>
        {
            lock (gate)
            {
                order.Add(task.Url);
            }
            return Task.CompletedTask;
        };

        Assert.IsTrue(_service!.Retry(parent.Id));

        await Task.WhenAll(children.Select(c => WaitForStatusAsync(c.Id, TaskStatus.Completed)));

        CollectionAssert.AreEqual(children.Select(c => c.Url).ToList(), order);
    }

    private sealed class FakeExecutor : ITaskExecutor
    {
        public Func<TaskItem, Action<double>, CancellationToken, Task> Handler { get; set; } =
            (_, _, _) => Task.CompletedTask;

        public Task<TaskExecutionResult> ExecuteAsync(TaskItem task, Action<double> progress, CancellationToken cancellationToken) =>
            Handler(task, progress, cancellationToken).ContinueWith(
                _ => TaskExecutionResult.Completed, cancellationToken);
    }
}
