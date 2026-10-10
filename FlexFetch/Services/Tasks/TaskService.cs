using System.Collections.Concurrent;
using System.Threading.Channels;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using Serilog;
using ILogger = Serilog.ILogger;
using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.Services.Tasks;

/// <summary>
/// Download queue and concurrency control: a global FIFO queue, a configurable
/// concurrency limit, per-task cancellation propagation, startup recovery,
/// parent-child aggregation and automatic retry with backoff.
/// </summary>
public sealed class TaskService : IDisposable
{
    private readonly ITaskRepository _tasks;
    private readonly IShareRepository _shares;
    private readonly LiteDbStore _store;
    private readonly IConfiguration _config;
    private readonly ITaskExecutor _executor;
    private readonly StorageService _storage;
    private readonly ILogger _log;

    private readonly Channel<TaskItem> _queue = Channel.CreateUnbounded<TaskItem>(
        new UnboundedChannelOptions { SingleReader = true });

    // ChannelReader.Count is unsupported for unbounded channels, so the
    // queued count is maintained with an interlocked counter.
    private int _queuedCount;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<string, Task> _activeProcesses = new();

    // Ids of tasks that currently sit in the queue or are being executed.
    // Every enqueue path (submit, retry, startup recovery) must pass through
    // TryEnqueue: recovery can run long after startup (component installs),
    // by which time newly submitted tasks already wait in the channel, and a
    // parent retry re-queues children that may still be queued there. Without
    // the marker each of those paths creates a second channel entry for the
    // same task - the duplicate downloads again hours later and its stale
    // in-memory copy rewrites the record over the fresh Completed state.
    private readonly ConcurrentDictionary<string, byte> _enqueued = new();

    private readonly SemaphoreSlim _concurrency;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _workerLoop;

    public TaskService(
        ITaskRepository tasks,
        IShareRepository shares,
        LiteDbStore store,
        IConfiguration config,
        ITaskExecutor executor,
        StorageService storage,
        ILogger log)
    {
        _tasks = tasks;
        _shares = shares;
        _store = store;
        _config = config;
        _executor = executor;
        _storage = storage;
        _log = log;
        _concurrency = new SemaphoreSlim(GetConcurrencyLimit());
        _workerLoop = WorkerLoopAsync();
    }

    public int ConcurrencyLimit => _concurrency.CurrentCount + _running.Count;

    public int RunningCount => _running.Count;

    public int QueuedCount => Volatile.Read(ref _queuedCount);

    /// <summary>
    /// Puts a task into the queue unless an entry for it is already pending
    /// or running. The marker is released when its process finishes, so a
    /// later retry can queue it again.
    /// </summary>
    private bool TryEnqueue(TaskItem task)
    {
        if (!_enqueued.TryAdd(task.Id, 0))
        {
            _log.Information("Task {TaskId} is already queued or running; not enqueued again", task.Id);
            return false;
        }

        if (_queue.Writer.TryWrite(task))
        {
            Interlocked.Increment(ref _queuedCount);
            return true;
        }

        _enqueued.TryRemove(task.Id, out _);
        return false;
    }

    /// <summary>Submits a new task: queues it and returns its id.</summary>
    public string Submit(
        string ownerUserId,
        string url,
        string? downloaderType = null,
        string? parentId = null,
        string? title = null,
        string? referrer = null,
        string? storageFolder = null)
    {
        var task = new TaskItem
        {
            OwnerUserId = ownerUserId,
            Url = url,
            DownloaderType = downloaderType,
            ParentId = parentId,
            Referrer = referrer,
            StorageFolder = storageFolder,
            Status = TaskStatus.Queued,
        };
        if (!string.IsNullOrWhiteSpace(title))
        {
            task.FileName = FileNameRules.Sanitize(title);
        }

        _tasks.Insert(task);
        TryEnqueue(task);

        _log.Information("Task {TaskId} queued for {Url}", task.Id, url);
        return task.Id;
    }

    public TaskItem? GetById(string id) => _tasks.GetById(id);

    public IReadOnlyList<TaskItem> GetByOwner(string ownerUserId) => _tasks.GetByOwner(ownerUserId);

    /// <summary>Re-queues a task for retry (per-user retry).</summary>
    public bool Retry(string id)
    {
        var task = _tasks.GetById(id);
        if (task is null)
        {
            return false;
        }

        // Virtual parents are aggregation containers for their children:
        // retrying them must re-queue the failed children, not re-run the
        // parent (re-expanding the list would duplicate the child tasks).
        if (task.IsVirtual)
        {
            var children = _tasks.GetChildren(id);
            var retried = 0;
            foreach (var child in children)
            {
                if (child.Status is not (TaskStatus.Failed or TaskStatus.Queued))
                {
                    continue;
                }

                child.Status = TaskStatus.Queued;
                child.Attempts = 0;
                child.ErrorMessage = null;
                _tasks.Update(child);
                TryEnqueue(child);
                retried++;
            }

            if (retried > 0)
            {
                _log.Information("Task {TaskId} re-queued {Count} child tasks for retry", id, retried);
                AggregateParent(id);
                return true;
            }

            return false;
        }

        // Retry applies to failed tasks and to tasks still sitting queued
        // (e.g. after an unexpected service stop the status may be left
        // queued without an error message): re-queueing restarts them.
        var isFailed = task.Status == TaskStatus.Failed;
        var isQueued = task.Status == TaskStatus.Queued;
        if (!isFailed && !isQueued)
        {
            return false;
        }

        task.Status = TaskStatus.Queued;
        task.Attempts = 0;
        task.ErrorMessage = null;
        _tasks.Update(task);
        TryEnqueue(task);

        _log.Information("Task {TaskId} re-queued for retry", id);
        return true;
    }

    /// <summary>
    /// Deletes a task: removes its records in one fast transaction and
    /// answers immediately. The slow parts - cancelling runs (each Cancel
    /// synchronously executes the yt-dlp kill registered on the token; a
    /// process-tree kill can block for minutes) and removing files - run in
    /// a background cleanup so the API never waits on them.
    /// </summary>
    public bool Delete(string id)
    {
        var task = _tasks.GetById(id);
        if (task is null)
        {
            return false;
        }

        var children = _tasks.GetChildren(id);

        // All records commit as one database transaction (one journal flush):
        // a large playlist's per-child deletes used to cost one flush each.
        _store.Database.BeginTrans();
        try
        {
            foreach (var child in children)
            {
                _shares.DeleteByTaskId(child.Id);
                _tasks.Delete(child.Id);
            }

            _shares.DeleteByTaskId(id);
            _tasks.Delete(id);
            _store.Database.Commit();
        }
        catch
        {
            _store.Database.Rollback();
            throw;
        }

        // Capture the file locations and run ids before the records are
        // gone: the cleanup needs the group folder and children's names.
        var folder = task.StorageFolder ?? task.Id;
        var deletesWholeFolder = task.ParentId is null;
        var childFileNames = children
            .Where(c => !string.IsNullOrEmpty(c.FileName))
            .Select(c => c.FileName!)
            .ToList();
        var runIds = children.Select(c => c.Id).Append(id).ToList();

        CleanupInBackground(task.Id, folder, deletesWholeFolder, runIds, childFileNames);
        _log.Information("Task {TaskId} deleted ({ChildCount} children)", id, children.Count);
        return true;
    }

    private readonly ConcurrentDictionary<string, byte> _cleanupsInFlight = new();

    /// <summary>
    /// Cancels the deleted task's runs and removes its files off the request
    /// thread. Duplicate calls for the same task (double click) clean only
    /// once; a directory left behind when the app exits mid-cleanup is
    /// removed by the periodic orphan-directory sweep.
    /// </summary>
    private void CleanupInBackground(
        string id,
        string folder,
        bool deletesWholeFolder,
        IReadOnlyList<string> runIds,
        IReadOnlyList<string> childFileNames)
    {
        if (!_cleanupsInFlight.TryAdd(id, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var runId in runIds)
                {
                    CancelRun(runId);
                }

                if (deletesWholeFolder)
                {
                    // A root task owns the group folder with all its files.
                    await DeleteDirBestEffortAsync(_storage.GetTaskDir(folder));
                    return;
                }

                // A deleted child takes only its own file out of the group
                // folder; the folder itself stays.
                await DeleteFileBestEffortAsync(_storage.GetTaskFilePath(folder, id + ".part"));
                foreach (var fileName in childFileNames)
                {
                    await DeleteFileBestEffortAsync(_storage.GetTaskFilePath(folder, fileName));
                    await DeleteFileBestEffortAsync(
                        _storage.GetTaskFilePath(folder, Path.GetFileNameWithoutExtension(fileName) + ".part"));
                }
            }
            finally
            {
                _cleanupsInFlight.TryRemove(id, out _);
            }
        });
    }

    private static async Task DeleteFileBestEffortAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (Exception)
            {
                // A still-dying download may hold the file; retry once, then
                // leave the rest to the periodic sweep.
                if (attempt >= 1)
                {
                    return;
                }

                await Task.Delay(2000);
            }
        }
    }

    private void CancelRun(string taskId)
    {
        if (_running.TryGetValue(taskId, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Task {TaskId}: cancellation callback failed during delete", taskId);
            }
        }
    }

    private static async Task DeleteDirBestEffortAsync(string dir)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }

                return;
            }
            catch (Exception)
            {
                // A still-dying download may hold a file; the cancel above
                // finishes within moments - retry once, then leave the rest
                // to the periodic orphan-directory sweep.
                if (attempt >= 1)
                {
                    return;
                }

                await Task.Delay(2000);
            }
        }
    }

    /// <summary>
    /// Recovers tasks after a restart: tasks left in Running, Queued or
    /// Waiting are marked Waiting - recovered and visible, but NOT enqueued.
    /// They start only when the startup sequence calls
    /// <see cref="ReleaseWaitingTasks"/> after the component installs, so a
    /// recovered download never runs against a missing yt-dlp or browser.
    /// Failed is reserved for real download failures and is not re-queued.
    /// The merged set is handled oldest first, so playlist children keep
    /// their front-to-back order across a restart (the in-memory queue alone
    /// does not survive it). Recovery can run long after startup (component
    /// installs delay it) - tasks that were submitted in the meantime and
    /// already wait in the channel, or execute right now, are not orphans
    /// and must be left alone: re-queueing them runs every download twice.
    /// </summary>
    public int RecoverPending()
    {
        var orphans = _tasks.GetByStatus(TaskStatus.Running)
            .Concat(_tasks.GetByStatus(TaskStatus.Queued))
            .Concat(_tasks.GetByStatus(TaskStatus.Waiting))
            .Where(t => !t.IsVirtual)
            .OrderBy(t => t.CreatedAt)
            .ToList();

        var recoveredCount = 0;
        var parents = new HashSet<string>();
        foreach (var task in orphans)
        {
            if (_enqueued.ContainsKey(task.Id))
            {
                _log.Information("Task {TaskId} is already queued or running; not recovered again", task.Id);
                continue;
            }

            // "Ytdlp" is the generic fallback a previous attempt happened to
            // pick, not an intentional pin (playlist expansion never pins
            // it): clear it so recovery re-selects by URL instead of walking
            // straight into the same fallback again.
            if (task.DownloaderType == "Ytdlp")
            {
                task.DownloaderType = null;
            }

            task.Status = TaskStatus.Waiting;
            task.Attempts = 0;
            task.ErrorMessage = null;
            _tasks.Update(task);
            if (task.ParentId is not null)
            {
                parents.Add(task.ParentId);
            }

            _log.Information("Task {TaskId} recovered after restart, waiting for components", task.Id);
            recoveredCount++;
        }

        foreach (var parentId in parents)
        {
            AggregateParent(parentId);
        }

        return recoveredCount;
    }

    /// <summary>
    /// Releases the tasks <see cref="RecoverPending"/> held in Waiting: they
    /// become Queued and enter the queue, oldest first so playlist children
    /// keep their order. Called on every startup path once the component
    /// installs are done - including installs skipped (all ready), disabled
    /// and failed - so held tasks can never strand.
    /// </summary>
    public int ReleaseWaitingTasks()
    {
        var waiting = _tasks.GetByStatus(TaskStatus.Waiting)
            .Where(t => !t.IsVirtual)
            .OrderBy(t => t.CreatedAt)
            .ToList();

        var parents = new HashSet<string>();
        foreach (var task in waiting)
        {
            task.Status = TaskStatus.Queued;
            _tasks.Update(task);
            if (task.ParentId is not null)
            {
                parents.Add(task.ParentId);
            }

            TryEnqueue(task);
        }

        foreach (var parentId in parents)
        {
            AggregateParent(parentId);
        }

        if (waiting.Count > 0)
        {
            _log.Information("Released {Count} waiting tasks into the queue", waiting.Count);
        }

        return waiting.Count;
    }

    /// <summary>
    /// Adopts the on-disk file size for completed tasks whose recorded size
    /// does not match it (e.g. the size was lost in a download-report
    /// encoding mismatch). The file name needs no reconciliation in the
    /// group layout: the recorded name IS the disk name inside the group
    /// folder. Runs at startup, before the queue resumes.
    /// </summary>
    public int ReconcileFileNames()
    {
        var fixedCount = 0;
        foreach (var task in _tasks.GetByStatus(TaskStatus.Completed))
        {
            if (string.IsNullOrEmpty(task.FileName))
            {
                continue;
            }

            var path = _storage.GetTaskFilePath(task, task.FileName);
            if (!File.Exists(path))
            {
                continue;
            }

            var size = new FileInfo(path).Length;
            if (task.FileSize == size)
            {
                continue;
            }

            task.FileSize = size;
            _tasks.Update(task);
            fixedCount++;
        }

        return fixedCount;
    }

    /// <summary>
    /// Aggregates a parent task from its children: overall progress and
    /// status, summarizing child errors on failure.
    /// </summary>
    public void AggregateParent(string parentId)
    {
        var parent = _tasks.GetById(parentId);
        if (parent is null)
        {
            return;
        }

        var children = _tasks.GetChildren(parentId);
        if (children.Count == 0)
        {
            return;
        }

        parent.Progress = children.Average(c => c.Progress);

        if (children.All(c => c.Status == TaskStatus.Completed))
        {
            parent.Status = TaskStatus.Completed;
            parent.ErrorMessage = null;
            parent.CompletedAt = DateTime.UtcNow;
        }
        else if (children.Any(c => c.Status == TaskStatus.Failed))
        {
            parent.Status = TaskStatus.Failed;
            parent.ErrorMessage = string.Join("; ", children
                .Where(c => c.Status == TaskStatus.Failed)
                .Select(c => $"#{c.Id}: {c.ErrorMessage ?? "unknown error"}"));
        }
        else if (children.Any(c => c.Status is TaskStatus.Queued or TaskStatus.Running))
        {
            parent.Status = TaskStatus.Running;
            parent.ErrorMessage = null;
        }
        else if (children.Any(c => c.Status is TaskStatus.Waiting))
        {
            parent.Status = TaskStatus.Waiting;
            parent.ErrorMessage = null;
        }

        _tasks.Update(parent);
    }

    public void Dispose()
    {
        _shutdown.Cancel();

        // Cancel running tasks but leave their status unchanged (Running/
        // Queued); a restart's RecoverPending re-queues them. Failed is
        // reserved for real download failures. Deleted tasks no longer exist
        // in the store and are skipped.
        foreach (var (id, cts) in _running)
        {
            cts.Cancel();
        }

        // Wait for in-flight process tasks to finish so they no longer touch
        // the store when the data directory is disposed/removed.
        try
        {
            Task.WaitAll(_activeProcesses.Values.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Individual task failures were already logged; ignore here.
        }

        _workerLoop.Wait(TimeSpan.FromSeconds(5));
        _shutdown.Dispose();
        _concurrency.Dispose();
    }

    private int GetConcurrencyLimit()
    {
        var raw = ConfigRegistry.From(_config, ConfigKeys.MaxConcurrency);
        return int.TryParse(raw, out var n) && n > 0 ? n : 2;
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var task))
                {
                    Interlocked.Decrement(ref _queuedCount);
                    await _concurrency.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    _activeProcesses[task.Id] = ProcessAsync(task);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    private async Task ProcessAsync(TaskItem task)
    {
        try
        {
            await ExecuteWithRetryAsync(task);
        }
        finally
        {
            _activeProcesses.TryRemove(task.Id, out _);
            _concurrency.Release();
            // The run is over (completed, failed or skipped): the id may be
            // queued again by a later retry.
            _enqueued.TryRemove(task.Id, out _);
        }
    }

    private async Task ExecuteWithRetryAsync(TaskItem task)
    {
        var maxRetries = GetMaxRetries();
        var attempt = 0;

        while (true)
        {
            attempt++;
            // A task deleted while queued stays in the in-memory channel (a
            // channel cannot drop individual items). Its record is gone -
            // skip it instead of re-downloading an invisible zombie. A record
            // already completed means this entry is a duplicate (a leftover
            // from before enqueue dedup, or written by an older build):
            // running it would download again and its stale in-memory copy
            // would rewrite the record over the fresh Completed state.
            var current = _tasks.GetById(task.Id);
            if (current is null)
            {
                _log.Information("Task {TaskId} was deleted while queued; skipping", task.Id);
                return;
            }

            if (current.Status == TaskStatus.Completed)
            {
                _log.Information("Task {TaskId} is already completed; skipping duplicate queue entry", task.Id);
                return;
            }

            task.Attempts = attempt;
            task.Status = TaskStatus.Running;
            task.ErrorMessage = null;
            task.StartedAt ??= DateTime.UtcNow;
            _tasks.Update(task);

            var cts = new CancellationTokenSource();
            _running[task.Id] = cts;

            try
            {
                var result = await _executor.ExecuteAsync(task, p => UpdateProgress(task, p), cts.Token);
                if (result == TaskExecutionResult.Expanded)
                {
                    // The parent became a virtual aggregation container;
                    // keep it Running until its children finish.
                    task.IsVirtual = true;
                    task.Status = TaskStatus.Running;
                    task.Progress = 0;
                    _tasks.Update(task);
                    _log.Information("Task {TaskId} expanded into child tasks", task.Id);
                    return;
                }

                task.Status = TaskStatus.Completed;
                task.Progress = 100;
                task.CompletedAt = DateTime.UtcNow;
                task.ErrorMessage = null;
                _tasks.Update(task);
                _log.Information("Task {TaskId} completed", task.Id);
                if (task.ParentId is not null)
                {
                    AggregateParent(task.ParentId);
                }
                return;
            }
            catch (OperationCanceledException)
            {
                // The task was cancelled: either deleted (it no longer exists
                // in the store - write nothing) or the service is shutting
                // down (Dispose cancelled the work; status stays Running so
                // a restart's RecoverPending re-queues it).
                if (_tasks.GetById(task.Id) is null)
                {
                    return;
                }

                task.Status = TaskStatus.Running;
                task.ErrorMessage = null;
                _tasks.Update(task);
                _log.Information("Task {TaskId} interrupted, will recover after restart", task.Id);
                if (task.ParentId is not null)
                {
                    AggregateParent(task.ParentId);
                }
                return;
            }
            catch (AuthRequiredException ex)
            {
                // Authentication/restriction errors are deterministic: cookies
                // were already attached (or were unavailable), so retrying the
                // whole task will not help. Fail immediately.
                task.ErrorMessage = ex.Message;
                task.Status = TaskStatus.Failed;
                _tasks.Update(task);
                _log.Warning(ex, "Task {TaskId} failed with auth/restriction error", task.Id);
                if (task.ParentId is not null)
                {
                    AggregateParent(task.ParentId);
                }
                return;
            }
            catch (RetryableException ex)
            {
                // Transient failure (timeout, connection, 5xx, rate limit):
                // retry up to maxRetries with backoff.
                task.ErrorMessage = ex.Message;
                _log.Warning(ex, "Task {TaskId} attempt {Attempt} failed (retryable)", task.Id, attempt);

                if (attempt <= maxRetries)
                {
                    task.Status = TaskStatus.Queued;
                    _tasks.Update(task);
                    try
                    {
                        await Task.Delay(GetBackoffDelay(attempt), _shutdown.Token);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        return; // shutting down; task stays queued for next start
                    }
                    continue; // retry
                }

                task.Status = TaskStatus.Failed;
                _tasks.Update(task);
                if (task.ParentId is not null)
                {
                    AggregateParent(task.ParentId);
                }
                return;
            }
            catch (Exception ex)
            {
                // Deterministic error (bad URL, unsupported format, 4xx):
                // fail immediately without automatic retry.
                task.ErrorMessage = ex.Message;
                task.Status = TaskStatus.Failed;
                _tasks.Update(task);
                _log.Warning(ex, "Task {TaskId} failed (not retried)", task.Id);
                if (task.ParentId is not null)
                {
                    AggregateParent(task.ParentId);
                }
                return;
            }
            finally
            {
                _running.TryRemove(task.Id, out _);
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// Persists download progress. Downloaders report a 0..1 fraction, while
    /// Progress is stored on a 0..100 scale (the completion paths write 100
    /// and the UI divides by 100), so the callback is scaled here - the one
    /// funnel every downloader's progress passes through. The 1-point
    /// threshold keeps the original store-write cadence of one update per
    /// progress percent.
    /// </summary>
    private void UpdateProgress(TaskItem task, double progress)
    {
        var rounded = Math.Round(progress * 100, 2);
        if (Math.Abs(task.Progress - rounded) < 1)
        {
            return;
        }

        task.Progress = rounded;
        _tasks.Update(task);
    }

    private int GetMaxRetries()
    {
        var raw = ConfigRegistry.From(_config, ConfigKeys.MaxRetries);
        return int.TryParse(raw, out var n) && n >= 0 ? n : 3;
    }

    private static TimeSpan GetBackoffDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
}
