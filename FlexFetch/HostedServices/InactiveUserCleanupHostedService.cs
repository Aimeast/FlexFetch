using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Tasks;
using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// Periodically removes download resources (tasks and files) of accounts that
/// have been inactive beyond the configured threshold. Accounts themselves are
/// kept; a re-login starts from an empty state. Disabled when the threshold is 0.
/// </summary>
public sealed class InactiveUserCleanupHostedService : IntervalHostedService
{
    private readonly IConfigRepository _config;
    private readonly IUserRepository _users;
    private readonly TaskService _taskService;
    private readonly UserService _userService;

    public InactiveUserCleanupHostedService(
        IConfigRepository config,
        IUserRepository users,
        TaskService taskService,
        UserService userService,
        ILogger log)
        : base(log, "InactiveUserCleanup")
    {
        _config = config;
        _users = users;
        _taskService = taskService;
        _userService = userService;
    }

    protected override TimeSpan GetInterval() => TimeSpan.FromHours(24);

    protected override async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        var threshold = int.TryParse(Get(ConfigKeys.InactiveDays), out var days) ? days : 30;
        if (threshold <= 0)
        {
            return; // cleanup disabled
        }

        var now = DateTime.UtcNow;
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

        await Task.CompletedTask;
    }

    private string Get(string key) => _config.Get(key) ?? ConfigRegistry.GetDefault(key);
}
