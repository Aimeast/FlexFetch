using System.Net;
using System.Net.Http;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Session;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

/// <summary>
/// The authenticated GraphQL fallback: a tombstoned syndication answer for a
/// sensitive post must route through TweetResultByRestId with the imported
/// X session, and anonymous failures must surface as login-required.
/// </summary>
[TestClass]
public sealed class TwitterDownloaderGraphQLTests
{
    private static readonly ILogger Log = TestLog.Instance;

    private const string Tombstone = """{"__typename":"TweetTombstone","tombstone":{}}""";

    private const string PageWithoutMedia = "<html><head><title>wild rabbit on X / X</title></head><body></body></html>";

    private const string GraphQLPayload = """
        {"data":{"tweetResult":{"result":{"__typename":"TweetWithVisibilityResults","limitedActionResults":{"limited_actions":[]},
        "tweet":{"__typename":"Tweet",
        "core":{"user_results":{"result":{"__typename":"User","legacy":{"name":"Wild Rabbit","screen_name":"yeshengtutu"}}}},
        "legacy":{"full_text":"Great trick https://t.co/abc",
        "extended_entities":{"media":[
        {"type":"video","media_url_https":"https://pbs.twimg.com/amplify_video_thumb/222/img/T.jpg",
         "expanded_url":"https://x.com/yeshengtutu/status/111/video/1",
         "video_info":{"variants":[
           {"content_type":"application/x-mpegURL","url":"https://video.twimg.com/amplify_video/222/pl/x.m3u8?tag=29"},
           {"bitrate":432000,"content_type":"video/mp4","url":"https://video.twimg.com/amplify_video/222/vid/avc1/480x270/a.mp4?tag=29"},
           {"bitrate":1280000,"content_type":"video/mp4","url":"https://video.twimg.com/amplify_video/222/vid/avc1/1280x720/b.mp4?tag=29"}]}},
        {"type":"photo","media_url_https":"https://pbs.twimg.com/media/333.jpg","expanded_url":"https://x.com/yeshengtutu/status/111/photo/1"}
        ]}}}}}}}
        """;

    private const string HomeHtml =
        "<html><head><script src=\"https://abs.twimg.com/responsive-web/client-web/main.81e5a75c61531e58a.js\"></script></head></html>";

    private const string BundleJs =
        "e.exports={queryId:\"xZfFqfOmKQu_OXekGI59mg\",operationName:\"TweetResultByRestId\",operationType:\"query\","
        + "metadata:{featureSwitches:[\"creator_subscriptions_tweet_preview_api_enabled\","
        + "\"responsive_web_graphql_timeline_navigation_enabled\"],metadata:{}}}";

    private static TwitterSessionStore CreateStore(out string dataDir)
    {
        dataDir = TestApp.CreateTempDataDir();
        var storage = new StorageService(dataDir);
        var files = new SessionFileStore(storage, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), TestLog.Instance);
        var store = new TwitterSessionStore(files);
        return store;
    }

    private static HttpResponseMessage TextResponse(string content, int status = 200, string[]? setCookies = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(content),
        };
        foreach (var cookie in setCookies ?? Array.Empty<string>())
        {
            response.Headers.Add("Set-Cookie", cookie);
        }

        return response;
    }

    /// <summary>Routes the authenticated flow by URL. Records every graphql
    /// call (url + headers) for assertions.</summary>
    private sealed class FakeRouter
    {
        public readonly List<(string Url, IReadOnlyDictionary<string, string>? Headers)> GraphQLCalls = new();
        public int GraphqlStatusCounter;
        private readonly string _graphqlPayload;

        public FakeRouter(string graphqlPayload) => _graphqlPayload = graphqlPayload;

        public Task<HttpResponseMessage> SendAsync(
            string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            if (url == "https://x.com/" && headers is not null && headers.ContainsKey("Cookie"))
            {
                // The logged-in shell: sets the csrf cookie AND references the
                // main bundle (the descriptor scrape consumes the same page).
                return Task.FromResult(TextResponse(HomeHtml,
                    setCookies: new[] { "ct0=fresh-csrf; Path=/; Secure" }));
            }

            if (url == "https://x.com/")
            {
                return Task.FromResult(TextResponse(HomeHtml));
            }

            if (url.Contains("main.", StringComparison.Ordinal))
            {
                return Task.FromResult(TextResponse(BundleJs));
            }

            if (url.Contains("/i/api/graphql/", StringComparison.Ordinal))
            {
                GraphQLCalls.Add((url, headers));
                return Task.FromResult(TextResponse(_graphqlPayload));
            }

            throw new InvalidOperationException("unexpected url: " + url);
        }

        /// <summary>Fails the FIRST graphql call with 400 (stale descriptor).</summary>
        public Task<HttpResponseMessage> SendWithStaleDescriptorAsync(
            string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        {
            if (url.Contains("/i/api/graphql/", StringComparison.Ordinal))
            {
                GraphQLCalls.Add((url, headers));
                return Task.FromResult(GraphqlStatusCounter++ == 0
                    ? TextResponse("{\"errors\":[{\"message\":\"variables could not be decoded\"}]}", 400)
                    : TextResponse(_graphqlPayload));
            }

            return SendAsync(url, headers, ct);
        }
    }

    [TestMethod]
    public async Task AnalyzeAsync_SensitivePost_SessionRoutesThroughGraphQL()
    {
        var store = CreateStore(out _);
        store.Write(new List<CookieItem> { new() { Domain = ".x.com", Name = "auth_token", Value = "t0ken" } },
            new TwitterSessionMeta { Account = "yeshengtutu" });
        var router = new FakeRouter(GraphQLPayload);
        var downloader = new TwitterDownloader(new DirectProxyService(), Log,
            fetchText: (url, _) => Task.FromResult(url.Contains("syndication") ? Tombstone : PageWithoutMedia),
            twitterSession: store,
            sendAsync: router.SendAsync);

        var analysis = await downloader.AnalyzeAsync("https://x.com/yeshengtutu/status/111", "task-1", CancellationToken.None);

        Assert.HasCount(2, analysis.Children);
        Assert.AreEqual("https://video.twimg.com/amplify_video/222/vid/avc1/1280x720/b.mp4?tag=29", analysis.Children[0].Url,
            "the highest-bitrate mp4 wins");
        Assert.AreEqual("https://pbs.twimg.com/media/333.jpg?name=orig", analysis.Children[1].Url,
            "photos are requested at original size");
        Assert.AreEqual("Great trick", analysis.Title, "the trailing t.co link is dropped from the title");
        Assert.AreEqual("https://x.com/yeshengtutu/status/111", analysis.Referrer);

        var (gqlUrl, gqlHeaders) = router.GraphQLCalls.Single();
        StringAssert.Contains(gqlUrl, "/i/api/graphql/xZfFqfOmKQu_OXekGI59mg/TweetResultByRestId");
        StringAssert.Contains(gqlUrl, Uri.EscapeDataString("\"tweetId\":\"111\""));
        StringAssert.Contains(gqlHeaders!["Authorization"], "Bearer ");
        Assert.AreEqual("fresh-csrf", gqlHeaders["x-csrf-token"]);
        StringAssert.Contains(gqlHeaders["Cookie"], "auth_token=t0ken");
        StringAssert.Contains(gqlHeaders["Cookie"], "ct0=fresh-csrf");
    }

    [TestMethod]
    public async Task AnalyzeAsync_SensitivePost_WithoutSession_FailsWithAuthRequired()
    {
        var downloader = new TwitterDownloader(new DirectProxyService(), Log,
            fetchText: (url, _) => Task.FromResult(url.Contains("syndication") ? Tombstone : PageWithoutMedia),
            twitterSession: null);

        var ex = await Assert.ThrowsExactlyAsync<AuthRequiredException>(
            () => downloader.AnalyzeAsync("https://x.com/yeshengtutu/status/111", "task-1", CancellationToken.None));

        Assert.AreEqual(AuthFailureReason.LoginRequired, ex.Reason);
        StringAssert.Contains(ex.Message, "Import an X session");
    }

    [TestMethod]
    public async Task AnalyzeAsync_RevokedSession_FallsBackAndDoesNotClaimAuthRequired()
    {
        var store = CreateStore(out _);
        store.Write(new List<CookieItem> { new() { Domain = ".x.com", Name = "auth_token", Value = "revoked" } },
            new TwitterSessionMeta());

        // x.com answers without setting ct0: the token is dead. The page
        // scrape then finds nothing - the session exists, so the error stays
        // a plain media failure (the session verdict is already Invalid).
        var downloader = new TwitterDownloader(new DirectProxyService(), Log,
            fetchText: (url, _) => Task.FromResult(url.Contains("syndication") ? Tombstone : PageWithoutMedia),
            twitterSession: store,
            sendAsync: (url, headers, ct) =>
                Task.FromResult(TextResponse("<html></html>")));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => downloader.AnalyzeAsync("https://x.com/yeshengtutu/status/111", "task-1", CancellationToken.None));

        var meta = store.ReadMeta();
        Assert.IsNotNull(meta);
        Assert.AreEqual(TwitterSessionHealth.Invalid.ToString(), meta.Health, "the refused token must be marked Invalid");
    }

    [TestMethod]
    public async Task AnalyzeAsync_GraphQL400Once_RefetchesDescriptorAndRetries()
    {
        var store = CreateStore(out _);
        store.Write(new List<CookieItem> { new() { Domain = ".x.com", Name = "auth_token", Value = "t0ken" } },
            new TwitterSessionMeta());
        var router = new FakeRouter(GraphQLPayload);
        var downloader = new TwitterDownloader(new DirectProxyService(), Log,
            fetchText: (url, _) => Task.FromResult(url.Contains("syndication") ? Tombstone : PageWithoutMedia),
            twitterSession: store,
            sendAsync: router.SendWithStaleDescriptorAsync);

        var analysis = await downloader.AnalyzeAsync("https://x.com/yeshengtutu/status/111", "task-1", CancellationToken.None);

        Assert.HasCount(2, analysis.Children);
        Assert.AreEqual(2, router.GraphQLCalls.Count, "the 400 must trigger exactly one descriptor refresh + retry");
    }

    [TestMethod]
    public async Task AnalyzeAsync_GraphQLPayloadWithCoreIdentity_ParsesAuthor()
    {
        // The current user shape carries the identity under
        // user_results.result.core, not result.legacy.
        const string payload = """
            {"data":{"tweetResult":{"result":{"__typename":"Tweet",
            "core":{"user_results":{"result":{"__typename":"User","core":{"name":"Wild Rabbit","screen_name":"yeshengtutu"}}}},
            "legacy":{"full_text":"New shape caption","extended_entities":{"media":[
            {"type":"video","media_url_https":"https://pbs.twimg.com/amplify_video_thumb/444/img/T.jpg",
             "video_info":{"variants":[{"bitrate":640000,"content_type":"video/mp4","url":"https://video.twimg.com/amplify_video/444/vid/avc1/640x360/c.mp4?tag=29"}]}}]}}}}}}
            """;
        var store = CreateStore(out _);
        store.Write(new List<CookieItem> { new() { Domain = ".x.com", Name = "auth_token", Value = "t0ken" } },
            new TwitterSessionMeta());
        var router = new FakeRouter(payload);
        var downloader = new TwitterDownloader(new DirectProxyService(), Log,
            fetchText: (url, _) => Task.FromResult(url.Contains("syndication") ? Tombstone : PageWithoutMedia),
            twitterSession: store,
            sendAsync: router.SendAsync);

        var analysis = await downloader.AnalyzeAsync("https://x.com/yeshengtutu/status/444", "task-1", CancellationToken.None);

        Assert.HasCount(1, analysis.Children);
        Assert.AreEqual("New shape caption", analysis.Title);
    }

    [TestMethod]
    public async Task AnalyzeAsync_GraphQLTombstone_FallsThroughToPage()
    {
        var store = CreateStore(out _);
        store.Write(new List<CookieItem> { new() { Domain = ".x.com", Name = "auth_token", Value = "t0ken" } },
            new TwitterSessionMeta());
        var router = new FakeRouter(Tombstone);
        var downloader = new TwitterDownloader(new DirectProxyService(), Log,
            fetchText: (url, _) => Task.FromResult(url.Contains("syndication") ? Tombstone : PageWithoutMedia),
            twitterSession: store,
            sendAsync: router.SendAsync);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => downloader.AnalyzeAsync("https://x.com/yeshengtutu/status/111", "task-1", CancellationToken.None));
    }
}
