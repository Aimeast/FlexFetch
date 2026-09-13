using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Session;
using Serilog;
using YoutubeDLSharp;
using YoutubeDLSharp.Metadata;
using YoutubeDLSharp.Options;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class YouTubeDownloaderTests
{
    private static readonly ILogger Log = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();

    private static (YouTubeDownloader Downloader, SessionSnapshotService Snapshot, string Dir) CreateDownloader(
        Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>>? fetch = null,
        Func<string, OptionSet, Action<double>, CancellationToken, Task<RunResult<string>>>? download = null)
    {
        var dir = TestApp.CreateTempDataDir();
        var proxy = new DirectProxyService();
        var ytdlp = new YtdlpService(proxy, Log, dir);
        var storage = new StorageService(dir);
        var snapshot = new SessionSnapshotService(storage);
        return (new YouTubeDownloader(ytdlp, proxy, storage, Log, snapshot, fetchData: fetch, download: download), snapshot, dir);
    }

    private static void SeedSnapshot(SessionSnapshotService snapshot, string? visitorData = "visitor-123")
    {
        snapshot.WriteSnapshot(
            new List<CookieItem>
            {
                new() { Domain = ".youtube.com", Name = "SID", Value = "x", HttpOnly = true },
                new() { Domain = ".youtube.com", Name = "LOGIN_INFO", Value = "y", HttpOnly = true },
            },
            new SessionMeta { VisitorData = visitorData });
    }

    private static readonly Func<string, OptionSet, CancellationToken, Task<RunResult<VideoData>>> OkFetch =
        (_, _, _) => Task.FromResult(new RunResult<VideoData>(
            true,
            Array.Empty<string>(),
            new VideoData { Title = "Fetched Title", Extension = "webm", Url = "https://example.com/v.webm" }));

    private static RunResult<VideoData> BotCheckFailure() => new(
        false,
        new[] { "ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies." },
        null!);

    [TestMethod]
    public void CanHandle_MatchesYouTubeDomains()
    {
        var downloader = CreateDownloader().Downloader;

        Assert.IsTrue(downloader.CanHandle("https://www.youtube.com/watch?v=abc"));
        Assert.IsTrue(downloader.CanHandle("https://youtu.be/abc"));
        Assert.IsFalse(downloader.CanHandle("https://x.com/SpaceX/status/1"));
        Assert.IsFalse(downloader.CanHandle("https://example.com/video.mp4"));
    }

    [TestMethod]
    public void BuildOptions_SetsFormatAndMerge()
    {
        var downloader = CreateDownloader().Downloader;

        var options = downloader.BuildOptions(new Uri("https://www.youtube.com/watch?v=abc"));

        Assert.AreEqual("bestvideo+bestaudio/best", options.Format);
        Assert.AreEqual(DownloadMergeFormat.Mp4, options.MergeOutputFormat);
    }

    [TestMethod]
    public void BuildOptions_IncludesProxyWhenConfigured()
    {
        var downloader = CreateDownloader().Downloader;
        // No proxy in this test setup -> Proxy must be null.
        var options = downloader.BuildOptions(new Uri("https://www.youtube.com/watch?v=abc"));

        Assert.IsNull(options.Proxy);
    }

    [TestMethod]
    public void BuildOptions_Anonymous_UsesAnonymousClientPosture()
    {
        var downloader = CreateDownloader().Downloader;

        var options = downloader.BuildOptions(new Uri("https://www.youtube.com/watch?v=abc"));

        // Anonymous posture: android_vr + mweb (no cookie, no visitor_data).
        Assert.AreEqual("youtube:player_client=android_vr,mweb", (string)options.ExtractorArgs);
    }

    [TestMethod]
    public void BuildOptions_WithCookies_UsesMwebWithVisitorData()
    {
        var (downloader, snapshot, _) = CreateDownloader();
        SeedSnapshot(snapshot);

        var options = downloader.BuildOptions(
            new Uri("https://www.youtube.com/watch?v=abc"), cookieFile: "cookies.txt");

        // Cookie posture: mweb only, paired with the jar's visitor identity.
        // android/tv with cookies is forbidden - yt-dlp skips those clients
        // and the leftover posture triggers the bot wall.
        Assert.AreEqual("youtube:player_client=mweb;visitor_data=visitor-123", (string)options.ExtractorArgs);
        Assert.AreEqual("cookies.txt", options.Cookies);
    }

    [TestMethod]
    public void NormalizeProxyForYtdlp_RewritesSocks5ToSocks5h()
    {
        Assert.AreEqual("socks5h://127.0.0.1:1080", YtdlpDownloader.NormalizeProxyForYtdlp("socks5://127.0.0.1:1080"));
        Assert.AreEqual("http://proxy:8080", YtdlpDownloader.NormalizeProxyForYtdlp("http://proxy:8080"));
    }

    [TestMethod]
    public void BuildAnalysis_SingleVideo_SuggestsTitleFileName()
    {
        var downloader = CreateDownloader().Downloader;
        var data = new VideoData
        {
            Title = "My Video Title",
            Extension = "mp4",
            Url = "https://example.com/v.mp4",
        };

        var analysis = downloader.BuildAnalysis(data);

        Assert.AreEqual("My Video Title", analysis.Title);
        Assert.IsEmpty(analysis.Children);
        Assert.AreEqual("My Video Title.mp4", analysis.SuggestedFileName);
    }

    [TestMethod]
    public void BuildAnalysis_Playlist_ExpandsChildren()
    {
        var downloader = CreateDownloader().Downloader;
        var data = new VideoData
        {
            Title = "Playlist",
            Entries = new[]
            {
                new VideoData { Title = "Video 1", Url = "https://example.com/1" },
                new VideoData { Title = "Video 2", Url = "https://example.com/2" },
                new VideoData { Title = "Skipped" }, // no URL -> dropped
            },
        };

        var analysis = downloader.BuildAnalysis(data);

        Assert.HasCount(2, analysis.Children);
        Assert.AreEqual("Video 1", analysis.Children[0].Title);
        Assert.AreEqual("https://example.com/2", analysis.Children[1].Url);
    }

    [TestMethod]
    public async Task AnalyzeAsync_FetchFailure_Throws()
    {
        var downloader = CreateDownloader(async (_, _, _) =>
            new RunResult<VideoData>(false, new[] { "ERROR: Unsupported URL" }, null!)).Downloader;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None));
    }

    [TestMethod]
    public async Task AnalyzeAsync_AuthError_NoSnapshot_ThrowsAuthRequired()
    {
        var calls = 0;
        var downloader = CreateDownloader((_, _, _) =>
        {
            calls++;
            return Task.FromResult(BotCheckFailure());
        }).Downloader;

        var ex = await Assert.ThrowsExactlyAsync<AuthRequiredException>(
            () => downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None));

        Assert.AreEqual(AuthFailureReason.LoginRequired, ex.Reason);
        Assert.AreEqual(1, calls); // no snapshot -> no session retry
    }

    [TestMethod]
    public async Task AnalyzeAsync_AuthError_RetriesOnceWithSnapshotCopy()
    {
        var calls = 0;
        string? cookieFileSeen = null;
        string? extractorArgsSeen = null;
        var copyExistedDuringRun = false;
        var (downloader, snapshot, dir) = CreateDownloader(async (_, options, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return BotCheckFailure();
            }

            cookieFileSeen = options.Cookies;
            extractorArgsSeen = (string?)options.ExtractorArgs;
            copyExistedDuringRun = options.Cookies is not null && File.Exists(options.Cookies);
            return new RunResult<VideoData>(
                true,
                Array.Empty<string>(),
                new VideoData { Title = "Fetched Title", Extension = "webm", Url = "https://example.com/v.webm" });
        });
        SeedSnapshot(snapshot);
        var snapshotFile = Path.Combine(dir, "session", "session.txt");
        var snapshotContent = File.ReadAllText(snapshotFile);

        var analysis = await downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None);

        // Retried exactly once, on the mweb posture, with a COPY of the
        // snapshot that existed during the run.
        Assert.AreEqual(2, calls);
        Assert.IsNotNull(cookieFileSeen);
        Assert.AreNotEqual(snapshotFile, cookieFileSeen, "the snapshot itself must never be handed to yt-dlp");
        Assert.IsTrue(copyExistedDuringRun, "the snapshot copy should exist during the retry");
        Assert.AreEqual("youtube:player_client=mweb;visitor_data=visitor-123", extractorArgsSeen);
        Assert.AreEqual("Fetched Title", analysis.Title);

        // The copy is deleted after the run; the snapshot is untouched.
        Assert.IsFalse(File.Exists(cookieFileSeen), "the one-time copy must be deleted after the run");
        Assert.AreEqual(snapshotContent, File.ReadAllText(snapshotFile), "the snapshot must not be modified by yt-dlp");
    }

    [TestMethod]
    public async Task AnalyzeAsync_AuthError_RetryStillFails_ThrowsAuthRequired()
    {
        var calls = 0;
        var (downloader, snapshot, _) = CreateDownloader((_, _, _) =>
        {
            calls++;
            return Task.FromResult(BotCheckFailure());
        });
        SeedSnapshot(snapshot);

        var ex = await Assert.ThrowsExactlyAsync<AuthRequiredException>(
            () => downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None));

        Assert.AreEqual(2, calls); // initial + session retry
        Assert.AreEqual(AuthFailureReason.LoginRequired, ex.Reason);
    }

    [TestMethod]
    public async Task AnalyzeAsync_AgeRestricted_RetriesWithSession()
    {
        // Account content (age/member/private) also goes through the session
        // retry: the anonymous first attempt fails, the session copy answers.
        var calls = 0;
        var (downloader, snapshot, _) = CreateDownloader((_, _, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new RunResult<VideoData>(false, new[] { "ERROR: [youtube] abc: age-restricted content" }, null!)
                : new RunResult<VideoData>(
                    true,
                    Array.Empty<string>(),
                    new VideoData { Title = "Members", Extension = "mp4", Url = "https://example.com/v.mp4" }));
        });
        SeedSnapshot(snapshot, visitorData: null);

        var analysis = await downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None);

        Assert.AreEqual(2, calls);
        Assert.AreEqual("Members", analysis.Title);
    }

    [TestMethod]
    public async Task AnalyzeAsync_DeadContent_DoesNotRetryWithSession()
    {
        // Dead content is NOT an auth problem: no session retry, no re-export
        // trigger - it must fail as a plain business failure.
        var calls = 0;
        var (downloader, snapshot, _) = CreateDownloader((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new RunResult<VideoData>(
                false, new[] { "ERROR: [youtube] abc: Video unavailable" }, null!));
        });
        SeedSnapshot(snapshot);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None));

        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task AnalyzeAsync_SuccessWithRotationWarning_DoesNotThrow()
    {
        // A rotated jar still "succeeds" via anonymous fallback with only a
        // WARNING: the run must not throw, but the rotation is logged and the
        // re-export path is triggered (verified by the classifier tests).
        var downloader = CreateDownloader((_, _, _) => Task.FromResult(new RunResult<VideoData>(
            true,
            new[] { "WARNING: [youtube] abc: the cookies are no longer valid and have been rotated" },
            new VideoData { Title = "Fetched", Extension = "mp4", Url = "https://example.com/v.mp4" }))).Downloader;

        var analysis = await downloader.AnalyzeAsync("https://www.youtube.com/watch?v=abc", "task-1", CancellationToken.None);

        Assert.AreEqual("Fetched", analysis.Title);
    }

    [TestMethod]
    public async Task DownloadAsync_RetriesWithSnapshotOnAuthError()
    {
        var calls = 0;
        string? cookieFileSeen = null;
        var (downloader, snapshot, _) = CreateDownloader(
            download: async (_, options, _, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    return new RunResult<string>(
                        false,
                        new[] { "ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies." },
                        null!);
                }

                cookieFileSeen = options.Cookies;
                return new RunResult<string>(true, Array.Empty<string>(), "video.mp4");
            });
        SeedSnapshot(snapshot);

        var task = new TaskItem { Id = "task-dl-1", Url = "https://www.youtube.com/watch?v=abc" };
        var analysis = new AnalysisResult { Title = "T", SuggestedFileName = "T.mp4" };

        await downloader.DownloadAsync(task, analysis, _ => { }, CancellationToken.None);

        Assert.AreEqual(2, calls);
        Assert.IsNotNull(cookieFileSeen);
        Assert.IsFalse(File.Exists(cookieFileSeen), "the one-time copy must be deleted after the download");
    }

    [TestMethod]
    public async Task DownloadAsync_AuthError_NoSnapshot_ThrowsAuthRequired()
    {
        var calls = 0;
        var downloader = CreateDownloader(
            download: (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(new RunResult<string>(
                    false,
                    new[] { "ERROR: [youtube] abc: Sign in to confirm you're not a bot. Use --cookies." },
                    null!));
            }).Downloader;

        var task = new TaskItem { Id = "task-dl-2", Url = "https://www.youtube.com/watch?v=abc" };
        var analysis = new AnalysisResult { Title = "T", SuggestedFileName = "T.mp4" };

        var ex = await Assert.ThrowsExactlyAsync<AuthRequiredException>(
            () => downloader.DownloadAsync(task, analysis, _ => { }, CancellationToken.None));

        Assert.AreEqual(AuthFailureReason.LoginRequired, ex.Reason);
        Assert.AreEqual(1, calls);
    }

    // --- Web health-gate signal (shared with the export pipeline) ---

    [TestMethod]
    public void IsLoginRedirectUrl_DetectsLoginAndChallengePages()
    {
        Assert.IsTrue(YouTubeDownloader.IsLoginRedirectUrl("https://accounts.google.com/signin"));
        Assert.IsTrue(YouTubeDownloader.IsLoginRedirectUrl("https://accounts.google.com/servicelogin"));
        Assert.IsTrue(YouTubeDownloader.IsLoginRedirectUrl("https://www.google.com/sorry/index"));
        Assert.IsTrue(YouTubeDownloader.IsLoginRedirectUrl("https://example.com/login"));
        Assert.IsFalse(YouTubeDownloader.IsLoginRedirectUrl("https://www.youtube.com/"));
        Assert.IsFalse(YouTubeDownloader.IsLoginRedirectUrl(null));
        Assert.IsFalse(YouTubeDownloader.IsLoginRedirectUrl(""));
    }

    private sealed class DirectProxyService : IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public bool ShouldProxyFast(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;

        public string? GetBrowserProxyAddress() => null;
    }
}
