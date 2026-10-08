using FlexFetch.Entities;
using FlexFetch.Services.Downloaders;

namespace FlexFetch.Services;

/// <summary>
/// File storage layout: every root task (a direct download or a list
/// download with all its children) owns one directory under files/, named
/// "{taskId} {title prefix}" so downloads are findable on the NAS. Children
/// store their files inside the parent's directory. Tasks carry the resolved
/// folder name in StorageFolder; legacy records (and anything unstamped)
/// fall back to the bare task id.
/// </summary>
public sealed class StorageService
{
    /// <summary>Byte budget for the readable title part of a folder name.</summary>
    private const int TitleBytes = 48;

    private readonly string _filesRoot;

    public StorageService(string dataDir)
    {
        DataDir = dataDir;
        _filesRoot = Path.Combine(dataDir, "files");
        Directory.CreateDirectory(_filesRoot);
    }

    /// <summary>Root data directory (independent of the program directory).</summary>
    public string DataDir { get; }

    /// <summary>Directory holding download files organized by task ID.</summary>
    public string FilesRoot => _filesRoot;

    /// <summary>Builds the stable folder name for a root task.</summary>
    public static string BuildFolderName(string taskId, string? title)
    {
        var prefix = string.IsNullOrWhiteSpace(title)
            ? string.Empty
            : " " + FileNameRules.Shorten(title, TitleBytes);
        return taskId + prefix;
    }

    /// <summary>The directory holding a task's file (its group's folder).</summary>
    public string GetTaskDir(TaskItem task) => Path.Combine(_filesRoot, task.StorageFolder ?? task.Id);

    public string GetTaskDir(string folderName) => Path.Combine(_filesRoot, folderName);

    public string GetTaskFilePath(TaskItem task, string fileName) =>
        Path.Combine(GetTaskDir(task), fileName);

    public string GetTaskFilePath(string folderName, string fileName) =>
        Path.Combine(GetTaskDir(folderName), fileName);

    public void EnsureTaskDir(TaskItem task) => Directory.CreateDirectory(GetTaskDir(task));

    public void EnsureTaskDir(string folderName) => Directory.CreateDirectory(GetTaskDir(folderName));

    public void DeleteTaskFiles(string folderName)
    {
        var dir = GetTaskDir(folderName);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
