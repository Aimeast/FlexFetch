using FlexFetch.Data;
using FlexFetch.Domain;
using FlexFetch.Services;
using Serilog;
using TaskStatus = FlexFetch.Domain.TaskStatus;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class GenericFileDownloaderTests
{
    private string? _dir;
    private StorageService? _storage;
    private GenericFileDownloader? _downloader;

    private static readonly ILogger Log = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "flexfetch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _storage = new StorageService(_dir);
        _downloader = new GenericFileDownloader(
            new DirectProxyService(),
            _storage,
            new InMemoryConfigRepository(),
            Log);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_dir is not null && Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Download_UsesContentDispositionFileName()
    {
        var body = new byte[] { 1, 2, 3, 4, 5 };
        using var server = new TestHttpServer(req => new TestHttpServer.HttpResponse(
            200, body, new Dictionary<string, string>
            {
                ["Content-Disposition"] = "attachment; filename=\"report.pdf\"",
            }));

        var task = new TaskItem { Id = "t1", Url = server.BaseUrl + "/file", OwnerUserId = "u" };
        var analysis = new FlexFetch.Services.Downloaders.AnalysisResult
        {
            Title = "file",
            DirectUrl = task.Url,
        };

        await _downloader!.DownloadAsync(task, analysis, _ => { }, CancellationToken.None);

        Assert.AreEqual("report.pdf", task.FileName);
        Assert.AreEqual(5, task.FileSize);
        Assert.IsTrue(File.Exists(_storage!.GetTaskFilePath("t1", "report.pdf")));
        CollectionAssert.AreEqual(body, File.ReadAllBytes(_storage.GetTaskFilePath("t1", "report.pdf")));
    }

    [TestMethod]
    public async Task Download_InfersFileNameFromUrl()
    {
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(200, new byte[] { 1 }));

        var task = new TaskItem { Id = "t2", Url = server.BaseUrl + "/videos/clip.bin", OwnerUserId = "u" };
        var analysis = new FlexFetch.Services.Downloaders.AnalysisResult { Title = "clip.bin", DirectUrl = task.Url };

        await _downloader!.DownloadAsync(task, analysis, _ => { }, CancellationToken.None);

        Assert.AreEqual("clip.bin", task.FileName);
        Assert.IsTrue(File.Exists(_storage!.GetTaskFilePath("t2", "clip.bin")));
    }

    [TestMethod]
    public async Task Download_ReportsProgress()
    {
        var body = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(200, body));

        var task = new TaskItem { Id = "t3", Url = server.BaseUrl + "/big.bin", OwnerUserId = "u" };
        var analysis = new FlexFetch.Services.Downloaders.AnalysisResult { Title = "big.bin", DirectUrl = task.Url };
        var lastProgress = -1.0;

        await _downloader!.DownloadAsync(task, analysis, p => lastProgress = p, CancellationToken.None);

        Assert.AreEqual(1.0, lastProgress, 0.01);
    }

    [TestMethod]
    public async Task Download_ResumesFromExistingPart()
    {
        var full = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
        const int offset = 40_000;
        using var server = new TestHttpServer(req =>
        {
            if (req.Headers.TryGetValue("Range", out var range))
            {
                var from = long.Parse(range.Split('=')[1].TrimEnd('-'));
                var slice = full[(int)from..];
                return new TestHttpServer.HttpResponse(206, slice, new Dictionary<string, string>
                {
                    ["Content-Range"] = $"bytes {from}-{full.Length - 1}/{full.Length}",
                    ["Accept-Ranges"] = "bytes",
                });
            }

            return new TestHttpServer.HttpResponse(200, full);
        });

        // Pre-seed the part file so the downloader continues from byte 40000.
        _storage!.EnsureTaskDir("t4");
        File.WriteAllBytes(_storage.GetTaskFilePath("t4", "t4.part"), full[..offset]);

        var task = new TaskItem { Id = "t4", Url = server.BaseUrl + "/resume.bin", OwnerUserId = "u" };
        var analysis = new FlexFetch.Services.Downloaders.AnalysisResult { Title = "resume.bin", DirectUrl = task.Url };

        await _downloader!.DownloadAsync(task, analysis, _ => { }, CancellationToken.None);

        var sentRange = server.Requests.Select(r => r.Headers.GetValueOrDefault("Range")).FirstOrDefault();
        StringAssert.StartsWith(sentRange, "bytes=40000");
        CollectionAssert.AreEqual(full, File.ReadAllBytes(_storage.GetTaskFilePath("t4", "resume.bin")));
    }

    [TestMethod]
    public async Task Download_RetriesWithReferrerOn403()
    {
        var attempts = 0;
        using var server = new TestHttpServer(req =>
        {
            attempts++;
            if (attempts == 1)
            {
                return new TestHttpServer.HttpResponse(403, Array.Empty<byte>());
            }

            return new TestHttpServer.HttpResponse(200, new byte[] { 9, 9, 9 });
        });

        var task = new TaskItem { Id = "t5", Url = server.BaseUrl + "/guarded.bin", OwnerUserId = "u" };
        var analysis = new FlexFetch.Services.Downloaders.AnalysisResult
        {
            Title = "guarded.bin",
            DirectUrl = task.Url,
        };

        await _downloader!.DownloadAsync(task, analysis, _ => { }, CancellationToken.None);

        Assert.AreEqual(2, attempts);
        Assert.AreEqual(3, task.FileSize);
        var referer = server.Requests[1].Headers.GetValueOrDefault("Referer");
        Assert.AreEqual(server.BaseUrl + "/guarded.bin", referer);
    }

    private sealed class DirectProxyService : IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };

        public string? GetProxyUri(Uri url) => null;
    }

    private sealed class InMemoryConfigRepository : IConfigRepository
    {
        private readonly Dictionary<string, string> _values = new();

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public IReadOnlyDictionary<string, string> GetAll() => _values;

        public void Set(string key, string value) => _values[key] = value;

        public bool Delete(string key) => _values.Remove(key);
    }
}
