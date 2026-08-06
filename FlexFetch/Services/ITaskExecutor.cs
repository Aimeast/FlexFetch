using FlexFetch.Domain;

namespace FlexFetch.Services;

/// <summary>
/// Executes a single download task. Implementations set the resulting
/// file name/size on the task and report progress (synchronous callback);
/// they throw on failure.
/// </summary>
public interface ITaskExecutor
{
    Task ExecuteAsync(TaskItem task, Action<double> progress, CancellationToken cancellationToken);
}
