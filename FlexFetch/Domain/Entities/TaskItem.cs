namespace FlexFetch.Domain;

public enum TaskStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>
/// A download task. Owned by a user; files are organized by task ID
/// (files/{taskId}/...). Parent tasks (e.g. playlists) aggregate children.
/// </summary>
public sealed class TaskItem
{
    public string Id { get; set; } = RandomId.New();

    public string OwnerUserId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>Source page to send as Referrer during download, if any.</summary>
    public string? Referrer { get; set; }

    public string? DownloaderType { get; set; }

    public string? FileName { get; set; }

    public long? FileSize { get; set; }

    public double Progress { get; set; }

    public TaskStatus Status { get; set; } = TaskStatus.Queued;

    public string? ErrorMessage { get; set; }

    public int Attempts { get; set; }

    /// <summary>
    /// True for tasks that are not persisted as real download jobs
    /// (e.g. parent aggregation tasks for playlists).
    /// </summary>
    public bool IsVirtual { get; set; }

    /// <summary>Id of the parent task, if any.</summary>
    public string? ParentId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
