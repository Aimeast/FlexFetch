using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using ILogger = Serilog.ILogger;
using TaskStatus = FlexFetch.Enums.TaskStatus;

namespace FlexFetch.Tests;

[TestClass]
public sealed class GenericFileDownloaderTests
{
    private string? _dir;
    private StorageService? _storage;
    private GenericFileDownloader? _downloader;

    private static readonly ILogger Log = TestLog.Instance;

    [TestInitialize]
    public void Setup()
    {
        _dir = TestApp.CreateTempDataDir();
        _storage = new StorageService(_dir);
        _downloader = new GenericFileDownloader(
            new DirectProxyService(),
            _storage,
            new TestConfig(),
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
    public async Task Download_Http500_ThrowsRetryable()
    {
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(500, Array.Empty<byte>()));
        var task = new TaskItem { Id = "t5", Url = server.BaseUrl + "/file", OwnerUserId = "u" };
        var analysis = new FlexFetch.Services.Downloaders.AnalysisResult { Title = "f", DirectUrl = task.Url };

        await Assert.ThrowsExactlyAsync<RetryableException>(
            () => _downloader!.DownloadAsync(task, analysis, _ => { }, CancellationToken.None));
    }

    [TestMethod]
    public async Task Download_Http404_ThrowsInvalidOperation()
    {
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(404, Array.Empty<byte>()));
        var task = new TaskItem { Id = "t6", Url = server.BaseUrl + "/missing", OwnerUserId = "u" };
        var analysis = new FlexFetch.Services.Downloaders.AnalysisResult { Title = "f", DirectUrl = task.Url };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _downloader!.DownloadAsync(task, analysis, _ => { }, CancellationToken.None));
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

    [TestMethod]
    public async Task Analyze_PrefersContentDispositionName()
    {
        // Share-style endpoints advertise the real name in the disposition
        // (inline, RFC 5987 for non-ASCII) even though the URL path names no
        // file - the analysis must pick it up, not the literal path segment.
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            Array.Empty<byte>(),
            new Dictionary<string, string>
            {
                ["Content-Type"] = "video/mp4",
                ["Content-Disposition"] = "inline; filename*=utf-8''%E5%8D%81%E6%9C%88%E5%85%AD%E6%97%A5.mp4",
            }));

        var analysis = await _downloader!.AnalyzeAsync(
            server.BaseUrl + "/api/share/ScQlfS50/file?taskId=0t3XtUSF", "t1", CancellationToken.None);

        // The disposition name arrives percent-encoded (filename*) and the
        // probe decodes it: the real title, not the literal path segment.
        Assert.AreEqual("\u5341\u6708\u516d\u65e5.mp4", analysis.Title);
        Assert.AreEqual("\u5341\u6708\u516d\u65e5.mp4", analysis.SuggestedFileName);
    }

    [TestMethod]
    public async Task Analyze_AppendsExtensionFromContentType()
    {
        // No advertised name: the URL segment ("file") gains the extension
        // the served Content-Type maps to, instead of a bare "file".
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            200,
            Array.Empty<byte>(),
            new Dictionary<string, string> { ["Content-Type"] = "video/mp4" }));

        var analysis = await _downloader!.AnalyzeAsync(
            server.BaseUrl + "/api/share/abc/file?taskId=xyz", "t1", CancellationToken.None);

        Assert.AreEqual("file.mp4", analysis.SuggestedFileName);
    }

    [TestMethod]
    public async Task Analyze_KeepsUrlNameWhenProbeFails()
    {
        // A failed probe never fails the analysis: the plain URL-inferred
        // name is kept (the download still re-resolves from the response).
        using var server = new TestHttpServer(_ => new TestHttpServer.HttpResponse(
            500, Array.Empty<byte>()));

        var analysis = await _downloader!.AnalyzeAsync(
            server.BaseUrl + "/api/share/abc/file?taskId=xyz", "t1", CancellationToken.None);

        Assert.AreEqual("file", analysis.SuggestedFileName);
    }

    private sealed class DirectProxyService : IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public bool ShouldProxyFast(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };

        public string? GetProxyUri(Uri url) => null;

    }
}
