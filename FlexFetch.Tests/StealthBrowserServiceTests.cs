using FlexFetch.Services;
using FlexFetch.Services.Downloaders;

namespace FlexFetch.Tests;

[TestClass]
public sealed class StealthBrowserServiceTests
{
    [TestMethod]
    public void FindFirstExisting_ReturnsExistingPath()
    {
        var dir = TestApp.CreateTempDataDir();
        try
        {
            var existing = Path.Combine(dir, "chrome.exe");
            File.WriteAllText(existing, "x");
            var missing = Path.Combine(dir, "missing.exe");

            var result = StealthBrowserService.FindFirstExisting(new[] { missing, existing });

            Assert.AreEqual(existing, result);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void FindFirstExisting_ReturnsNullWhenNoneExist()
    {
        var dir = TestApp.CreateTempDataDir();
        try
        {
            var result = StealthBrowserService.FindFirstExisting(new[] { Path.Combine(dir, "a.exe"), Path.Combine(dir, "b.exe") });

            Assert.IsNull(result);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void GetBrowserCandidates_ReturnsNonEmptyList()
    {
        Assert.IsGreaterThan(0, StealthBrowserService.GetBrowserCandidates().Count);
    }

    [TestMethod]
    public void BuildSelfCheckScript_ProducesValidJsonShape()
    {
        var script = StealthBrowserService.BuildSelfCheckScript();

        StringAssert.Contains(script, "navigator.webdriver");
        StringAssert.Contains(script, "cdc_");
        StringAssert.Contains(script, "userAgent");
    }

    [TestMethod]
    public void ParseSelfCheckResults_CleanBrowser_AllPass()
    {
        var json = """
        {
          "webdriver": false,
          "cdc": false,
          "userAgent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/126.0",
          "brands": "Chromium,Google Chrome",
          "hardwareConcurrency": 8,
          "windowSize": "1366x768",
          "plugins": 5,
          "languages": "en-US,en"
        }
        """;

        var results = StealthBrowserService.ParseSelfCheckResults(json);

        Assert.HasCount(8, results);
        Assert.IsTrue(results.All(r => r.Passed));
    }

    [TestMethod]
    public void ParseSelfCheckResults_DetectsHeadlessVectors()
    {
        var json = """
        {
          "webdriver": true,
          "cdc": true,
          "userAgent": "HeadlessChrome/126.0",
          "brands": "HeadlessChrome",
          "hardwareConcurrency": 1,
          "windowSize": "800x600",
          "plugins": 0,
          "languages": ""
        }
        """;

        var results = StealthBrowserService.ParseSelfCheckResults(json);

        Assert.IsFalse(results.Single(r => r.Name == "navigator.webdriver").Passed);
        Assert.IsFalse(results.Single(r => r.Name == "cdc_ marker").Passed);
        Assert.IsFalse(results.Single(r => r.Name == "User-Agent").Passed);
        Assert.IsFalse(results.Single(r => r.Name == "brands").Passed);
        Assert.IsFalse(results.Single(r => r.Name == "hardwareConcurrency").Passed);
        Assert.IsFalse(results.Single(r => r.Name == "window size").Passed);
    }

    [TestMethod]
    public void IsMediaResponse_DetectsByContentType()
    {
        Assert.IsTrue(BrowserParsingDownloader.IsMediaResponse(
            new Dictionary<string, string> { ["Content-Type"] = "video/mp4" }, "https://cdn.example.com/clip"));
        Assert.IsTrue(BrowserParsingDownloader.IsMediaResponse(
            new Dictionary<string, string> { ["Content-Type"] = "audio/mpeg" }, "https://cdn.example.com/audio"));
        Assert.IsTrue(BrowserParsingDownloader.IsMediaResponse(
            new Dictionary<string, string> { ["Content-Type"] = "application/vnd.apple.mpegurl" }, "https://cdn.example.com/stream.m3u8"));
        Assert.IsTrue(BrowserParsingDownloader.IsMediaResponse(
            new Dictionary<string, string> { ["Content-Type"] = "application/dash+xml" }, "https://cdn.example.com/manifest.mpd"));
        Assert.IsFalse(BrowserParsingDownloader.IsMediaResponse(
            new Dictionary<string, string> { ["Content-Type"] = "text/html" }, "https://example.com/page"));
        Assert.IsFalse(BrowserParsingDownloader.IsMediaResponse(
            new Dictionary<string, string> { ["Content-Type"] = "application/json" }, "https://example.com/api"));
    }

    [TestMethod]
    public void IsMediaResponse_FallsBackToExtension()
    {
        // Unknown binary content type with a media extension still counts.
        Assert.IsTrue(BrowserParsingDownloader.IsMediaResponse(
            new Dictionary<string, string> { ["Content-Type"] = "application/octet-stream" }, "https://cdn.example.com/clip.mp4"));
        // No content type but a media extension counts.
        Assert.IsTrue(BrowserParsingDownloader.IsMediaResponse(
            new Dictionary<string, string>(), "https://cdn.example.com/video.webm"));
    }

    [TestMethod]
    public void HasMediaExtension_MatchesCommonMediaExtensions()
    {
        Assert.IsTrue(BrowserParsingDownloader.HasMediaExtension("https://cdn.example.com/clip.mp4"));
        Assert.IsTrue(BrowserParsingDownloader.HasMediaExtension("https://cdn.example.com/stream.m3u8?token=1"));
        Assert.IsTrue(BrowserParsingDownloader.HasMediaExtension("https://cdn.example.com/audio.mp3"));
        Assert.IsFalse(BrowserParsingDownloader.HasMediaExtension("https://example.com/page"));
        Assert.IsFalse(BrowserParsingDownloader.HasMediaExtension("https://example.com/app.js"));
    }
}
