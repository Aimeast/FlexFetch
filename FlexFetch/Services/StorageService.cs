namespace FlexFetch.Services;

/// <summary>
/// File storage layout: download files are organized by task ID
/// (files/{taskId}/...). No user-space directory hierarchy.
/// </summary>
public sealed class StorageService
{
    private readonly string _filesRoot;

    public StorageService(string dataDir)
    {
        _filesRoot = Path.Combine(dataDir, "files");
        Directory.CreateDirectory(_filesRoot);
    }

    public string GetTaskDir(string taskId) => Path.Combine(_filesRoot, taskId);

    public string GetTaskFilePath(string taskId, string fileName) =>
        Path.Combine(GetTaskDir(taskId), fileName);

    public void EnsureTaskDir(string taskId) => Directory.CreateDirectory(GetTaskDir(taskId));

    public void DeleteTaskFiles(string taskId)
    {
        var dir = GetTaskDir(taskId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
