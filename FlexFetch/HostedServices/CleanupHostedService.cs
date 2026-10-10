using System.Text.RegularExpressions;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Services;
using FlexFetch.Services.Tasks;
using ILogger = Serilog.ILogger;
using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.HostedServices;

/// <summary>
/// Periodically performs all data cleanup:
/// 1. removes expired share tokens and orphaned group folders (directories
///    under files/ whose root task no longer exists) plus stale download
///    leftovers inside live folders;
/// 2. removes download resources (tasks and files) of accounts inactive
///    beyond the configured threshold - accounts themselves are kept, and a
///    re-login starts from an empty state (disabled when the threshold is 0);
/// 3. removes expired anonymous guest sessions with all their tasks and
///    files (disabled when the threshold is 0).
/// </summary>
public sealed partial class CleanupHostedService : IntervalHostedService
{
    private readonly IShareRepository _shares;
    private readonly ITaskRepository _tasks;
    private readonly IConfiguration _config;
    private readonly IUserRepository _users;
    private readonly IGuestRepository _guests;
    private readonly TaskService _taskService;
    private readonly UserService _userService;
    private readonly StorageService _storage;

    public CleanupHostedService(
        IShareRepository shares,
        ITaskRepository tasks,
        IConfiguration config,
        IUserRepository users,
        IGuestRepository guests,
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
        _guests = guests;
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

        // Orphaned group folders: "{taskId} {title prefix}" directories whose
        // root task no longer exists (the task id is the leading token; ids
        // never contain spaces).
        var taskDirs = Directory.Exists(_storage.FilesRoot)
            ? Directory.EnumerateDirectories(_storage.FilesRoot).ToList()
            : new List<string>();
        foreach (var dir in taskDirs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var folderName = Path.GetFileName(dir);
            var taskId = folderName.Split(' ')[0];
            if (_tasks.GetById(taskId) is null)
            {
                _storage.DeleteTaskFiles(folderName);
                continue;
            }

            // Stale download leftovers inside a live group folder (yt-dlp
            // fragments/part files from a crashed run). A conservative age
            // guard keeps them away from any download in progress.
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (!IsDownloadLeftover(name)
                    || File.GetLastWriteTimeUtc(file) >= now.AddHours(-24))
                {
                    continue;
                }

                TryDeleteFile(file);
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

        // Expired anonymous guest sessions: delete the session together with
        // all its tasks and files. Not time-critical - a session idle beyond
        // the threshold is removed by this sweep as soon as it runs, except
        // while it still has tasks queued, running or waiting for components.
        var guestHours = int.TryParse(Get(ConfigKeys.AnonymousSessionHours), out var hours) ? hours : 360;
        if (guestHours > 0)
        {
            var guestCutoff = now.AddHours(-guestHours);
            foreach (var guest in _guests.GetIdleBefore(guestCutoff))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var guestTasks = _taskService.GetByOwner(guest.Id);
                if (guestTasks.Any(t => t.Status is TaskStatus.Queued or TaskStatus.Running or TaskStatus.Waiting))
                {
                    continue;
                }

                foreach (var task in guestTasks)
                {
                    _taskService.Delete(task.Id);
                }

                _guests.Delete(guest.Id);
            }
        }

        await Task.CompletedTask;
    }

    private string Get(string key) => ConfigRegistry.From(_config, key);

    private static bool IsDownloadLeftover(string fileName) =>
        fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
        || FragmentRegex().IsMatch(fileName);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort: the next sweep round retries.
        }
    }

    [GeneratedRegex(@"\.f\d+\.")]
    private static partial Regex FragmentRegex();
}
