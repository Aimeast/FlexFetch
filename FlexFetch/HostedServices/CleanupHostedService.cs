using FlexFetch.Data;
using FlexFetch.Services;
using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// Periodically removes expired share tokens and orphaned task files
/// (files/{taskId} directories whose task no longer exists).
/// </summary>
public sealed class CleanupHostedService : IntervalHostedService
{
    private readonly IShareRepository _shares;
    private readonly ITaskRepository _tasks;
    private readonly StorageService _storage;

    public CleanupHostedService(IShareRepository shares, ITaskRepository tasks, StorageService storage, ILogger log)
        : base(log, "Cleanup")
    {
        _shares = shares;
        _tasks = tasks;
        _storage = storage;
    }

    protected override TimeSpan GetInterval() => TimeSpan.FromHours(6);

    protected override async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        // Expired share tokens.
        foreach (var share in _shares.GetExpired(now))
        {
            _shares.Delete(share.Token);
        }

        // Orphaned task directories: files/{taskId} with no matching task.
        var taskDirs = Directory.Exists(_storage.FilesRoot)
            ? Directory.EnumerateDirectories(_storage.FilesRoot).ToList()
            : new List<string>();
        foreach (var dir in taskDirs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var taskId = Path.GetFileName(dir);
            if (_tasks.GetById(taskId) is null)
            {
                _storage.DeleteTaskFiles(taskId);
            }
        }

        await Task.CompletedTask;
    }
}
