using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Tasks;
using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// Periodically performs all data cleanup:
/// 1. removes expired share tokens and orphaned task files (files/{taskId}
///    directories whose task no longer exists);
/// 2. removes download resources (tasks and files) of accounts inactive
///    beyond the configured threshold — accounts themselves are kept, and a
///    re-login starts from an empty state (disabled when the threshold is 0).
/// </summary>
public sealed class CleanupHostedService : IntervalHostedService
{
    private readonly IShareRepository _shares;
    private readonly ITaskRepository _tasks;
    private readonly IConfigRepository _config;
    private readonly IUserRepository _users;
    private readonly TaskService _taskService;
    private readonly UserService _userService;
    private readonly StorageService _storage;

    public CleanupHostedService(
        IShareRepository shares,
        ITaskRepository tasks,
        IConfigRepository config,
        IUserRepository users,
        TaskService taskService,
        UserService userService,
        StorageService storage,
        IHostApplicationLifetime lifetime,
        ILogger log)
        : base(lifetime, log, "Cleanup")
    {
        _shares = shares;
        _tasks = tasks;
        _config = config;
        _users = users;
        _taskService = taskService;
        _userService = userService;
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

        // Resources of accounts inactive beyond the threshold.
        var threshold = int.TryParse(Get(ConfigKeys.InactiveDays), out var days) ? days : 30;
        if (threshold > 0)
        {
            foreach (var user in _users.GetAll())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (!_userService.IsInactive(user, now))
                {
                    continue;
                }

                foreach (var task in _taskService.GetByOwner(user.Id))
                {
                    _taskService.Delete(task.Id);
                }
            }
        }

        await Task.CompletedTask;
    }

    private string Get(string key) => _config.Get(key) ?? ConfigRegistry.GetDefault(key);
}
