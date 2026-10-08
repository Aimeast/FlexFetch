using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Tasks;
using Serilog;

namespace FlexFetch.Tests;

[TestClass]
public sealed class DownloaderTaskExecutorTests
{
    private static readonly ILogger Log = TestLog.Instance;

    private string? _dir;
    private LiteDbStore? _store;
    private ITaskRepository? _tasks;
    private StorageService? _storage;

    [TestInitialize]
    public void Setup()
    {
        _dir = TestApp.CreateTempDataDir();
        _store = new LiteDbStore(Path.Combine(_dir, "flexfetch.db"));
        _tasks = new TaskRepository(_store);
        _storage = new StorageService(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _store?.Dispose();
        if (_dir is not null && Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_ManifestContentType_KeepsYtdlpFirst()
    {
        // An extension-less URL serving an HLS manifest must be parsed by
        // yt-dlp (HLS stream or playlist expansion), not saved as a raw
        // manifest file by the generic downloader.
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            Array.Empty<byte>(),
            new Dictionary<string, string> { ["Content-Type"] = "application/vnd.apple.mpegurl" }));

        var task = new TaskItem { OwnerUserId = "user-1", Url = server.BaseUrl + "/playlist" };
        var executor = CreateExecutor();

        await executor.ExecuteAsync(task, _ => { }, CancellationToken.None);

        Assert.AreEqual("Ytdlp", task.DownloaderType);
    }

    [TestMethod]
    public async Task ExecuteAsync_MediaContentType_PromotesGenericFirst()
    {
        // A plain direct media link (extension-less, video Content-Type) is
        // still handed to the generic file downloader right away.
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            Array.Empty<byte>(),
            new Dictionary<string, string> { ["Content-Type"] = "video/mp4" }));

        var task = new TaskItem { OwnerUserId = "user-1", Url = server.BaseUrl + "/clip" };
        var executor = CreateExecutor();

        await executor.ExecuteAsync(task, _ => { }, CancellationToken.None);

        Assert.AreEqual("Generic", task.DownloaderType);
    }

    [TestMethod]
    public async Task ExecuteAsync_PinnedDownloaderType_UsesItDirectly()
    {
        // A child task whose parent analysis already resolved the downloader
        // (e.g. a direct media play address pinned to "Generic") must use
        // exactly that downloader, skipping URL matching / probing / the
        // fallback chain entirely.
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            Array.Empty<byte>(),
            new Dictionary<string, string> { ["Content-Type"] = "text/html" }));

        var task = new TaskItem
        {
            OwnerUserId = "user-1",
            Url = server.BaseUrl + "/stream",
            DownloaderType = "Generic",
        };
        var executor = CreateExecutor();

        await executor.ExecuteAsync(task, _ => { }, CancellationToken.None);

        Assert.AreEqual("Generic", task.DownloaderType);
    }

    [TestMethod]
    public async Task ExecuteAsync_UnknownPinnedDownloader_Throws()
    {
        var task = new TaskItem
        {
            OwnerUserId = "user-1",
            Url = "https://example.com/x",
            DownloaderType = "NotARegisteredDownloader",
        };
        var executor = CreateExecutor();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(task, _ => { }, CancellationToken.None));
    }

    [TestMethod]
    public async Task ExecuteAsync_StampsTitleAndStorageFolder()
    {
        // The analysis title names the group folder: the task record carries
        // both so every later file operation finds the directory without
        // re-resolving the parent.
        var task = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/x" };
        var executor = CreateExecutor();

        await executor.ExecuteAsync(task, _ => { }, CancellationToken.None);

        Assert.AreEqual("t", task.Title);
        Assert.AreEqual(StorageService.BuildFolderName(task.Id, "t"), task.StorageFolder);
    }

    [TestMethod]
    public async Task ExecuteAsync_Expansion_ChildrenCarryParentFolder()
    {
        // Children are created after the parent's folder is stamped: each
        // child task stores its file in the parent group's folder, so a
        // whole list lands in one directory.
        var list = new FakeDownloader("List")
        {
            Title = "parent",
            Children = [new MediaChild { Url = "https://example.com/c1", Title = "c1" }],
        };
        var factory = new DownloaderFactory(new IDownloader[] { list, new FakeDownloader("Generic") });
        string? capturedFolder = null;
        var executor = new DownloaderTaskExecutor(
            factory,
            new DirectProxyService(),
            _tasks!,
            _storage!,
            Log,
            (parent, _, _) =>
            {
                capturedFolder = parent.StorageFolder;
                return "child-id";
            });

        var parent = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/list" };
        var result = await executor.ExecuteAsync(parent, _ => { }, CancellationToken.None);

        Assert.AreEqual(TaskExecutionResult.Expanded, result);
        Assert.AreEqual(StorageService.BuildFolderName(parent.Id, "parent"), parent.StorageFolder);
        Assert.AreEqual(parent.StorageFolder, capturedFolder);
    }

    [TestMethod]
    public async Task ExecuteAsync_ChildNameClash_SuffixesNewFileAndRenamesSibling()
    {
        // Same-titled entries in one group folder (browser/HTML expansion
        // shares one title): the finished sibling's file is renamed to carry
        // its id, and the new download is suffixed before it starts.
        var parent = new TaskItem { OwnerUserId = "user-1", Url = "https://example.com/list", IsVirtual = true };
        parent.StorageFolder = StorageService.BuildFolderName(parent.Id, "Show");
        _tasks!.Insert(parent);
        var sibling = new TaskItem
        {
            Id = "C001",
            OwnerUserId = "user-1",
            Url = "https://example.com/c1",
            ParentId = parent.Id,
            StorageFolder = parent.StorageFolder,
            FileName = "ep.mp4",
            Status = Enums.TaskStatus.Completed,
        };
        _tasks.Insert(sibling);
        Directory.CreateDirectory(_storage!.GetTaskDir(parent.StorageFolder));
        File.WriteAllText(_storage.GetTaskFilePath(sibling, "ep.mp4"), "old");

        var child = new TaskItem
        {
            Id = "C002",
            OwnerUserId = "user-1",
            Url = "https://example.com/c2",
            ParentId = parent.Id,
            StorageFolder = parent.StorageFolder,
        };
        var downloader = new FakeDownloader("Generic") { SuggestedFileName = "ep.mp4" };
        var executor = new DownloaderTaskExecutor(
            new DownloaderFactory(new IDownloader[] { downloader }),
            new DirectProxyService(),
            _tasks,
            _storage,
            Log);

        await executor.ExecuteAsync(child, _ => { }, CancellationToken.None);

        Assert.AreEqual("ep [C002].mp4", downloader.LastDownloadedName);
        Assert.IsTrue(File.Exists(_storage.GetTaskFilePath(child, "ep [C001].mp4")));
        Assert.IsFalse(File.Exists(_storage.GetTaskFilePath(child, "ep.mp4")));
        Assert.AreEqual("ep [C001].mp4", _tasks.GetById("C001")!.FileName);
    }

    private DownloaderTaskExecutor CreateExecutor(Func<TaskItem, MediaChild, string?, string>? submitChild = null)
    {
        var factory = new DownloaderFactory(new IDownloader[]
        {
            new FakeDownloader("Generic"),
            new FakeDownloader("Ytdlp"),
        });
        return new DownloaderTaskExecutor(factory, new DirectProxyService(), _tasks!, _storage!, Log, submitChild);
    }

    private sealed class FakeDownloader : IDownloader
    {
        public FakeDownloader(string type) => Type = type;

        public string Type { get; }

        public bool IsDomainSpecific => false;

        public string Title { get; set; } = "t";

        public string? SuggestedFileName { get; set; }

        public string? LastDownloadedName { get; private set; }

        public IReadOnlyList<MediaChild> Children { get; set; } = Array.Empty<MediaChild>();

        public bool CanHandle(string url) =>
            url.StartsWith("http", StringComparison.OrdinalIgnoreCase);

        public Task<AnalysisResult> AnalyzeAsync(string url, string taskId, CancellationToken ct) =>
            Task.FromResult(new AnalysisResult
            {
                Title = Title,
                DirectUrl = url,
                SuggestedFileName = SuggestedFileName,
                Children = Children,
            });

        public Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken ct)
        {
            LastDownloadedName = analysis.SuggestedFileName;
            return Task.CompletedTask;
        }
    }
}
