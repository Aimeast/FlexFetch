using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.Entities;

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

    /// <summary>Page/list title from analysis. Names the storage folder
    /// (files/{id} {title prefix}) so downloads are findable on disk.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Name of the directory under files/ holding this task's file: the
    /// parent task's folder for children, its own for roots. Null on legacy
    /// records, where the task id doubles as the folder name.
    /// </summary>
    public string? StorageFolder { get; set; }

    public string? DownloaderType { get; set; }

    /// <summary>Extracted page/tweet text, shown under the URL (e.g. tweet content).</summary>
    public string? ContentText { get; set; }

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
