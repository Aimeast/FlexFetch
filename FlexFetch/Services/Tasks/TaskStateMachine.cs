using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.Services.Tasks;

/// <summary>
/// Valid task state transitions. Illegal transitions are rejected.
/// </summary>
public static class TaskStateMachine
{
    private static readonly HashSet<(TaskStatus From, TaskStatus To)> Transitions = new()
    {
        (TaskStatus.Queued, TaskStatus.Running),
        (TaskStatus.Running, TaskStatus.Completed),
        (TaskStatus.Running, TaskStatus.Failed),
        // Retry: only failed tasks can go back to queued.
        (TaskStatus.Failed, TaskStatus.Queued),
        // Startup recovery: interrupted tasks are held until the component
        // installs finish, then released back into the queue.
        (TaskStatus.Running, TaskStatus.Waiting),
        (TaskStatus.Queued, TaskStatus.Waiting),
        (TaskStatus.Waiting, TaskStatus.Waiting),
        (TaskStatus.Waiting, TaskStatus.Queued),
    };

    public static bool CanTransition(TaskStatus from, TaskStatus to) =>
        Transitions.Contains((from, to));

    public static void EnsureTransition(TaskStatus from, TaskStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Illegal task state transition: {from} -> {to}");
        }
    }
}
