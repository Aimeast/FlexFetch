using FlexFetch.Domain;

namespace FlexFetch.Services;

/// <summary>Outcome of executing a single task.</summary>
public enum TaskExecutionResult
{
    /// <summary>The task was downloaded to completion.</summary>
    Completed = 0,

    /// <summary>The task was expanded into child tasks (e.g. playlist);
    /// the parent remains a virtual aggregation container.</summary>
    Expanded = 1,
}

/// <summary>
/// Executes a single download task. Implementations set the resulting
/// file name/size on the task and report progress (synchronous callback);
/// they throw on failure.
/// </summary>
public interface ITaskExecutor
{
    Task<TaskExecutionResult> ExecuteAsync(TaskItem task, Action<double> progress, CancellationToken cancellationToken);
}
