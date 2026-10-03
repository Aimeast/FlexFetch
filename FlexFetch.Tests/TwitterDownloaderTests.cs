using System.Net.Http;
using FlexFetch.Services.Downloaders;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class TwitterDownloaderTests
{
    private static readonly ILogger Log = TestLog.Instance;

    private static TwitterDownloader CreateDownloader(Func<string, CancellationToken, Task<string>> fetch) =>
        new(new DirectProxyService(), Log, fetch);

    [TestMethod]
    public void CanHandle_MatchesXDomains()
    {
        var downloader = new TwitterDownloader(new DirectProxyService(), Log);

        Assert.IsTrue(downloader.CanHandle("https://x.com/SpaceX/status/123"));
        Assert.IsTrue(downloader.CanHandle("https://twitter.com/SpaceX/status/123"));
        Assert.IsFalse(downloader.CanHandle("https://www.youtube.com/watch?v=abc"));
    }

    [TestMethod]
    public async Task AnalyzeAsync_Syndication_VideoPost_PicksBestMp4Variant()
    {
        var requested = new List<string>();
        const string syndication = """
            {"__typename":"Tweet","id_str":"111","text":"SpaceX launch footage","user":{"name":"Elon Musk","screen_name":"elonmusk"},
            "mediaDetails":[{"type":"video","media_url_https":"https://pbs.twimg.com/amplify_video_thumb/111/img/T.jpg",
              "video_info":{"aspect_ratio":[1,1],"duration_millis":16000,"variants":[
                {"content_type":"application/x-mpegURL","url":"https://video.twimg.com/amplify_video/111/pl/x.m3u8?tag=16"},
                {"bitrate":432000,"content_type":"video/mp4","url":"https://video.twimg.com/amplify_video/111/vid/avc1/320x320/a.mp4?tag=16"},
                {"bitrate":8768000,"content_type":"video/mp4","url":"https://video.twimg.com/amplify_video/111/vid/avc1/1080x1080/b.mp4?tag=16"}]}}]}
            """;
        var downloader = CreateDownloader((url, _) =>
        {
            requested.Add(url);
            return Task.FromResult(syndication);
        });

        var analysis = await downloader.AnalyzeAsync("https://x.com/elonmusk/status/111", "task-1", CancellationToken.None);

        CollectionAssert.Contains(requested, "https://cdn.syndication.twimg.com/tweet-result?id=111&token=a");
        Assert.HasCount(1, analysis.Children);
        Assert.AreEqual("https://video.twimg.com/amplify_video/111/vid/avc1/1080x1080/b.mp4?tag=16", analysis.Children[0].Url);
        Assert.AreEqual("Generic", analysis.Children[0].DownloaderType);
        // Children carry no title: they keep the CDN's original file name
        // (unique per media item, extension included).
        Assert.IsNull(analysis.Children[0].Title);
        Assert.AreEqual("SpaceX launch footage", analysis.Title);
        Assert.AreEqual("SpaceX launch footage", analysis.ContentText);
        Assert.AreEqual("https://x.com/elonmusk/status/111", analysis.Referrer);
    }

    [TestMethod]
    public async Task AnalyzeAsync_Syndication_RepostWithLinkOnlyBody_TakesTitleFromSourcePost()
    {
        const string repost = """
            {"__typename":"Tweet","id_str":"777","text":"https://t.co/uJvmOhuRK3","user":{"name":"Elon Musk","screen_name":"elonmusk"},
            "mediaDetails":[{"type":"video","expanded_url":"https://x.com/Teslaconomics/status/88888888888888888/video/1",
              "media_url_https":"https://pbs.twimg.com/amplify_video_thumb/111/img/T.jpg",
              "video_info":{"variants":[
                {"bitrate":8768000,"content_type":"video/mp4","url":"https://video.twimg.com/amplify_video/111/vid/avc1/1080x1080/b.mp4?tag=16"}]}}]}
            """;
        const string source = """
            {"__typename":"Tweet","id_str":"88888888888888888","text":"The return flight of that very same Starship","user":{"name":"Teslaconomics","screen_name":"Teslaconomics"}}
            """;
        var downloader = CreateDownloader((url, _) => Task.FromResult(url.Contains("id=88888888888888888&") ? source : repost));

        var analysis = await downloader.AnalyzeAsync("https://x.com/elonmusk/status/777", "task-1", CancellationToken.None);

        Assert.AreEqual("The return flight of that very same Starship", analysis.Title);
        Assert.AreEqual("The return flight of that very same Starship", analysis.ContentText);
        Assert.HasCount(1, analysis.Children);
        Assert.AreEqual("https://video.twimg.com/amplify_video/111/vid/avc1/1080x1080/b.mp4?tag=16", analysis.Children[0].Url);
    }

    [TestMethod]
    public async Task AnalyzeAsync_Syndication_RepostSourceUnavailable_FallsBackToAuthor()
    {
        const string repost = """
            {"__typename":"Tweet","id_str":"777","text":"https://t.co/uJvmOhuRK3","user":{"name":"Elon Musk","screen_name":"elonmusk"},
            "mediaDetails":[{"type":"video","expanded_url":"https://x.com/Teslaconomics/status/88888888888888888/video/1",
              "media_url_https":"https://pbs.twimg.com/amplify_video_thumb/111/img/T.jpg",
              "video_info":{"variants":[
                {"bitrate":8768000,"content_type":"video/mp4","url":"https://video.twimg.com/amplify_video/111/vid/avc1/1080x1080/b.mp4?tag=16"}]}}]}
            """;
        var downloader = CreateDownloader((url, _) => url.Contains("id=88888888888888888&")
            ? Task.FromException<string>(new HttpRequestException("404 for " + url))
            : Task.FromResult(repost));

        var analysis = await downloader.AnalyzeAsync("https://x.com/elonmusk/status/777", "task-1", CancellationToken.None);

        Assert.AreEqual("Elon Musk on X", analysis.Title);
        Assert.IsNull(analysis.ContentText);
        Assert.HasCount(1, analysis.Children);
    }

    [TestMethod]
    public async Task AnalyzeAsync_Syndication_MultiPhoto_ExpandsAllAtOriginalSize()
    {
        const string syndication = """
            {"__typename":"Tweet","id_str":"222","text":"Check out pictures","user":{"name":"NASA HQ PHOTO","screen_name":"nasahqphoto"},
            "mediaDetails":[
              {"type":"photo","media_url_https":"https://pbs.twimg.com/media/AAA1.jpg"},
              {"type":"photo","media_url_https":"https://pbs.twimg.com/media/AAA2.jpg"},
              {"type":"photo","media_url_https":"https://pbs.twimg.com/media/AAA3.jpg"},
              {"type":"photo","media_url_https":"https://pbs.twimg.com/media/AAA4.jpg"}]}
            """;
        var downloader = CreateDownloader((_, _) => Task.FromResult(syndication));

        var analysis = await downloader.AnalyzeAsync("https://x.com/nasahqphoto/status/222", "task-1", CancellationToken.None);

        Assert.HasCount(4, analysis.Children);
        Assert.IsTrue(analysis.Children.All(c => c.Url.EndsWith(".jpg?name=orig", StringComparison.Ordinal)));
        Assert.IsTrue(analysis.Children.All(c => c.DownloaderType == "Generic"));
        Assert.AreEqual("Check out pictures", analysis.Title);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "https://pbs.twimg.com/media/AAA1.jpg?name=orig",
                "https://pbs.twimg.com/media/AAA2.jpg?name=orig",
                "https://pbs.twimg.com/media/AAA3.jpg?name=orig",
                "https://pbs.twimg.com/media/AAA4.jpg?name=orig",
            },
            analysis.Children.Select(c => c.Url).ToList());
    }

    [TestMethod]
    public async Task AnalyzeAsync_Syndication_AnimatedGif_UsesMp4Variant()
    {
        const string syndication = """
            {"__typename":"Tweet","id_str":"333","text":"look at this","user":{"name":"Someone","screen_name":"someone"},
            "mediaDetails":[{"type":"animated_gif","media_url_https":"https://pbs.twimg.com/tweet_video_thumb/333/img/G.png",
              "video_info":{"variants":[
                {"bitrate":0,"content_type":"video/mp4","url":"https://video.twimg.com/tweet_video/333/s.mp4"},
                {"bitrate":1280000,"content_type":"video/mp4","url":"https://video.twimg.com/tweet_video/333/l.mp4"}]}}]}
            """;
        var downloader = CreateDownloader((_, _) => Task.FromResult(syndication));

        var analysis = await downloader.AnalyzeAsync("https://x.com/someone/status/333", "task-1", CancellationToken.None);

        Assert.HasCount(1, analysis.Children);
        Assert.AreEqual("https://video.twimg.com/tweet_video/333/l.mp4", analysis.Children[0].Url);
    }

    [TestMethod]
    public async Task AnalyzeAsync_Syndication_Unavailable_FallsBackToPageScrape()
    {
        const string page = """
            <html><head>
              <meta property="og:description" content="SpaceX launch footage">
              <meta property="og:image" content="https://pbs.twimg.com/ext_tw_video_thumb/111/img/T.jpg?format=jpg&amp;name=large">
            </head><body>
              <script>self.__next_f.push([1,"video_info:$R[66]={duration_millis:16000,variants:$R[67]=[
                $R[68]={content_type:"application/x-mpegURL",url:"https://video.twimg.com/ext_tw_video/111/pl/x.m3u8"},
                $R[69]={bitrate:432000,content_type:"video/mp4",url:"https://video.twimg.com/ext_tw_video/111/vid/avc1/320x320/a.mp4?tag=12"},
                $R[70]={bitrate:8768000,content_type:"video/mp4",url:"https://video.twimg.com/ext_tw_video/111/vid/avc1/1080x1080/b.mp4?tag=12"}]},
                quoted:$R[90]={variants:$R[91]=[$R[92]={bitrate:9900000,content_type:"video/mp4",url:"https://video.twimg.com/ext_tw_video/999/vid/avc1/720x720/c.mp4?tag=12"}]}"])</script>
            </body></html>
            """;
        var downloader = CreateDownloader((url, _) => url.Contains("syndication.twimg.com")
            ? Task.FromException<string>(new HttpRequestException("404 for " + url))
            : Task.FromResult(page));

        var analysis = await downloader.AnalyzeAsync("https://x.com/elonmusk/status/111", "task-1", CancellationToken.None);

        Assert.HasCount(1, analysis.Children);
        Assert.AreEqual("https://video.twimg.com/ext_tw_video/111/vid/avc1/1080x1080/b.mp4?tag=12", analysis.Children[0].Url);
        Assert.AreEqual("SpaceX launch footage", analysis.Title);
        Assert.AreEqual("https://x.com/elonmusk/status/111", analysis.Referrer);
    }

    [TestMethod]
    public async Task AnalyzeAsync_PageFallback_PhotoOgImage_AtOriginalSize()
    {
        const string page = """
            <html><head>
              <meta property="og:description" content="A photo post">
              <meta property="og:image" content="https://pbs.twimg.com/media/HTj9SCwWwAAGO78.jpg?format=jpg&amp;name=large">
              <meta property="og:image" content="https://pbs.twimg.com/media/NoExtension1?format=webp&amp;name=large">
            </head><body>no media here</body></html>
            """;
        var downloader = CreateDownloader((url, _) => url.Contains("syndication.twimg.com")
            ? Task.FromException<string>(new HttpRequestException("404 for " + url))
            : Task.FromResult(page));

        var analysis = await downloader.AnalyzeAsync("https://x.com/nasahqphoto/status/444", "task-1", CancellationToken.None);

        Assert.HasCount(2, analysis.Children);
        Assert.AreEqual("https://pbs.twimg.com/media/HTj9SCwWwAAGO78.jpg?format=jpg&name=orig", analysis.Children[0].Url);
        Assert.AreEqual("https://pbs.twimg.com/media/NoExtension1.jpg?format=jpg&name=orig", analysis.Children[1].Url);
        Assert.AreEqual("A photo post", analysis.Title);
    }

    [TestMethod]
    public async Task AnalyzeAsync_TextOnlyPost_FailsWithClearMessage()
    {
        const string syndication = """
            {"__typename":"Tweet","id_str":"555","text":"just text","user":{"name":"A","screen_name":"a"}}
            """;
        var downloader = CreateDownloader((url, _) => url.Contains("syndication.twimg.com")
            ? Task.FromResult(syndication)
            : throw new HttpRequestException("should not reach the page"));

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            downloader.AnalyzeAsync("https://x.com/a/status/555", "task-1", CancellationToken.None));

        StringAssert.Contains(exception.Message, "no downloadable media");
    }

    [TestMethod]
    public async Task AnalyzeAsync_UnresolvablePost_FailsWithClearMessage()
    {
        var downloader = CreateDownloader((url, _) => url.Contains("syndication.twimg.com")
            ? Task.FromException<string>(new HttpRequestException("404 for " + url))
            : Task.FromResult("<html><body>nothing</body></html>"));

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            downloader.AnalyzeAsync("https://x.com/a/status/666", "task-1", CancellationToken.None));

        StringAssert.Contains(exception.Message, "No downloadable media");
    }

    [TestMethod]
    public async Task AnalyzeAsync_StatusIdExtraction_IgnoresVideoSuffix()
    {
        var requested = new List<string>();
        var downloader = CreateDownloader((url, _) =>
        {
            requested.Add(url);
            return Task.FromResult("<html><body>nothing</body></html>");
        });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            downloader.AnalyzeAsync("https://x.com/a/status/1851515326581916096/video/1", "task-1", CancellationToken.None));

        CollectionAssert.Contains(requested, "https://cdn.syndication.twimg.com/tweet-result?id=1851515326581916096&token=a");
    }
}
