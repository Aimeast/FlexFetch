using FlexFetch.Domain;
using FlexFetch.Services.Downloaders;

namespace FlexFetch.Tests;

[TestClass]
public sealed class DownloaderFactoryTests
{
    [TestMethod]
    public void SelectDownloaders_ReturnsMatching_OrderedByPriority()
    {
        var high = new FakeDownloader("High", 100, "youtube.com");
        var low = new FakeDownloader("Low", 10, "youtube.com");
        var generic = new FakeDownloader("Generic", 0, "http");
        var factory = new DownloaderFactory(new IDownloader[] { generic, low, high });

        var selected = factory.SelectDownloaders("https://www.youtube.com/watch?v=abc");

        Assert.HasCount(3, selected);
        Assert.AreEqual("High", selected[0].Type);
        Assert.AreEqual("Low", selected[1].Type);
        Assert.AreEqual("Generic", selected[2].Type);
    }

    [TestMethod]
    public void SelectDownloaders_FiltersNonMatching()
    {
        var youtube = new FakeDownloader("YouTube", 100, "youtube.com");
        var generic = new FakeDownloader("Generic", 0, "http");
        var factory = new DownloaderFactory(new IDownloader[] { youtube, generic });

        var selected = factory.SelectDownloaders("https://example.com/file.bin");

        Assert.HasCount(1, selected);
        Assert.AreEqual("Generic", selected[0].Type);
    }

    [TestMethod]
    public void Fallback_IsLowestPriorityDownloader()
    {
        var high = new FakeDownloader("High", 100, "youtube.com");
        var generic = new FakeDownloader("Generic", 0, "http");
        var factory = new DownloaderFactory(new IDownloader[] { high, generic });

        Assert.AreEqual("Generic", factory.Fallback!.Type);
    }

    private sealed class FakeDownloader : IDownloader
    {
        private readonly string[] _needles;

        public FakeDownloader(string type, int priority, params string[] needles)
        {
            Type = type;
            Priority = priority;
            _needles = needles;
        }

        public string Type { get; }

        public int Priority { get; }

        public bool CanHandle(string url) =>
            _needles.Any(n => url.Contains(n, StringComparison.OrdinalIgnoreCase));

        public Task<AnalysisResult> AnalyzeAsync(string url, CancellationToken ct) =>
            Task.FromResult(new AnalysisResult { Title = "t", DirectUrl = url });

        public Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
