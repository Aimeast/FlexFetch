using System.Collections.Concurrent;
using System.Threading.Channels;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services;
using Serilog;
using TaskStatus = FlexFetch.Enums.TaskStatus;
using ILogger = Serilog.ILogger;

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
    private readonly IConfigRepository _config;
    private readonly ITaskExecutor _executor;
    private readonly StorageService _storage;
    private readonly ILogger _log;

    private readonly Channel<TaskItem> _queue = Channel.CreateUnbounded<TaskItem>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly SemaphoreSlim _concurrency;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _workerLoop;

    public TaskService(
        ITaskRepository tasks,
        IShareRepository shares,
        IConfigRepository config,
        ITaskExecutor executor,
        StorageService storage,
        ILogger log)
    {
        _tasks = tasks;
        _shares = shares;
        _config = config;
        _executor = executor;
        _storage = storage;
        _log = log;
        _concurrency = new SemaphoreSlim(GetConcurrencyLimit());
        _workerLoop = WorkerLoopAsync();
    }

    public int ConcurrencyLimit => _concurrency.CurrentCount + _running.Count;

    public int RunningCount => _running.Count;

    public int QueuedCount => _queue.Reader.Count;

    /// <summary>Submits a new task: queues it and returns its id.</summary>
    public string Submit(
        string ownerUserId,
        string url,
        string? downloaderType = null,
        string? parentId = null,
        string? title = null,
        string? referrer = null)
    {
        var task = new TaskItem
        {
            OwnerUserId = ownerUserId,
            Url = url,
            DownloaderType = downloaderType,
            ParentId = parentId,
            Referrer = referrer,
            Status = TaskStatus.Queued,
        };
        if (!string.IsNullOrWhiteSpace(title))
        {
            task.FileName = FileNameRules.Sanitize(title);
        }

        _tasks.Insert(task);
        _queue.Writer.TryWrite(task);
        _log.Information("Task {TaskId} queued for {Url}", task.Id, url);
        return task.Id;
    }

    public TaskItem? GetById(string id) => _tasks.GetById(id);

    public IReadOnlyList<TaskItem> GetByOwner(string ownerUserId) => _tasks.GetByOwner(ownerUserId);

    /// <summary>Re-queues a failed task for retry (per-user retry).</summary>
    public bool Retry(string id)
    {
        var task = _tasks.GetById(id);
        if (task is null || task.Status != TaskStatus.Failed)
        {
            return false;
        }

        task.Status = TaskStatus.Queued;
        task.Attempts = 0;
        task.ErrorMessage = null;
        _tasks.Update(task);
        _queue.Writer.TryWrite(task);
        _log.Information("Task {TaskId} re-queued for retry", id);
        return true;
    }

    /// <summary>Deletes a task: cancels it, cleans up files and share links.</summary>
    public bool Delete(string id)
    {
        var task = _tasks.GetById(id);
        if (task is null)
        {
            return false;
        }

        if (_running.TryGetValue(id, out var cts))
        {
            cts.Cancel();
        }

        foreach (var child in _tasks.GetChildren(id))
        {
            if (_running.TryGetValue(child.Id, out var childCts))
            {
                childCts.Cancel();
            }
        }

        // Remove children (and their files/shares) first, then the task itself.
        foreach (var child in _tasks.GetChildren(id))
        {
            _shares.DeleteByTaskId(child.Id);
            _storage.DeleteTaskFiles(child.Id);
            _tasks.Delete(child.Id);
        }

        _shares.DeleteByTaskId(id);
        _storage.DeleteTaskFiles(id);
        _tasks.Delete(id);
        _log.Information("Task {TaskId} deleted", id);
        return true;
    }

    /// <summary>
    /// Recovers tasks after a restart: tasks left in Running are reset to
    /// Queued and re-queued. Queued tasks are also re-queued.
    /// </summary>
    public void RecoverPending()
    {
        var orphans = _tasks.GetByStatus(TaskStatus.Running)
            .Concat(_tasks.GetByStatus(TaskStatus.Queued))
            .Where(t => !t.IsVirtual)
            .ToList();

        foreach (var task in orphans)
        {
            task.Status = TaskStatus.Queued;
            task.Attempts = 0;
            task.ErrorMessage = null;
            _tasks.Update(task);
            _queue.Writer.TryWrite(task);
            _log.Information("Task {TaskId} recovered after restart", task.Id);
        }
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
        else if (children.All(c => c.Status == TaskStatus.Cancelled))
        {
            parent.Status = TaskStatus.Cancelled;
            parent.ErrorMessage = null;
        }

        _tasks.Update(parent);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _workerLoop.Wait(TimeSpan.FromSeconds(5));
        _shutdown.Dispose();
        _concurrency.Dispose();
    }

    private int GetConcurrencyLimit()
    {
        var raw = _config.Get(ConfigKeys.MaxConcurrency) ?? ConfigRegistry.GetDefault(ConfigKeys.MaxConcurrency);
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
                    await _concurrency.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    _ = ProcessAsync(task);
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
            _concurrency.Release();
        }
    }

    private async Task ExecuteWithRetryAsync(TaskItem task)
    {
        var maxRetries = GetMaxRetries();
        var attempt = 0;

        while (true)
        {
            attempt++;
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
                task.Status = TaskStatus.Cancelled;
                task.ErrorMessage = null;
                _tasks.Update(task);
                _log.Information("Task {TaskId} cancelled", task.Id);
                if (task.ParentId is not null)
                {
                    AggregateParent(task.ParentId);
                }
                return;
            }
            catch (Exception ex)
            {
                task.ErrorMessage = ex.Message;
                _log.Warning(ex, "Task {TaskId} attempt {Attempt} failed", task.Id, attempt);

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
            finally
            {
                _running.TryRemove(task.Id, out _);
                cts.Dispose();
            }
        }
    }

    private void UpdateProgress(TaskItem task, double progress)
    {
        var rounded = Math.Round(progress, 2);
        if (Math.Abs(task.Progress - rounded) < 0.01)
        {
            return;
        }

        task.Progress = rounded;
        _tasks.Update(task);
    }

    private int GetMaxRetries()
    {
        var raw = _config.Get(ConfigKeys.MaxRetries) ?? ConfigRegistry.GetDefault(ConfigKeys.MaxRetries);
        return int.TryParse(raw, out var n) && n >= 0 ? n : 3;
    }

    private static TimeSpan GetBackoffDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
}
