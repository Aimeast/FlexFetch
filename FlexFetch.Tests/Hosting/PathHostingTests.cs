using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FlexFetch.Tests;

/// <summary>
/// Virtual-path hosting (web.pathBase): the pipeline strips the configured
/// URL prefix and the UI uses document-relative references, so the app
/// serves at the root and under the prefix at the same time.
/// </summary>
[TestClass]
public sealed class PathHostingTests
{
    private string? _dataDir;

    [TestInitialize]
    public void Setup() => _dataDir = TestApp.CreateTempDataDir();

    [TestCleanup]
    public void Cleanup()
    {
        if (_dataDir is not null && Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private static string WwwRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "FlexFetch", "wwwroot"));

    [TestMethod]
    public async Task PathBase_PrefixRequests_AreHandled()
    {
        using var factory = TestApp.CreateFactory(_dataDir!,
            settings: new Dictionary<string, string> { ["web:pathBase"] = "/flexfetch" },
            webRoot: WwwRoot());
        using var client = factory.CreateClient();

        // API and static assets under the prefix reach the same endpoints
        // and files as their root counterparts.
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/flexfetch/api/auth/status")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/flexfetch/app.js")).StatusCode);

        // The bare prefix redirects to the trailing-slash form: document-
        // relative references resolve against the nearest directory of the
        // document URL, and ".../flexfetch" without the slash would resolve
        // against the parent directory.
        using var noFollow = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var bare = await noFollow.GetAsync("/flexfetch");
        Assert.AreEqual(HttpStatusCode.Found, bare.StatusCode);
        Assert.AreEqual("/flexfetch/", bare.Headers.Location?.ToString());

        // The trailing-slash form serves the default page.
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/flexfetch/")).StatusCode);
    }

    [TestMethod]
    public async Task PathBase_RootRequests_StillWork()
    {
        // UsePathBase strips the prefix only when present: root hosting
        // keeps working so a deployment can move between the forms without
        // breaking either.
        using var factory = TestApp.CreateFactory(_dataDir!,
            settings: new Dictionary<string, string> { ["web:pathBase"] = "/flexfetch" },
            webRoot: WwwRoot());
        using var client = factory.CreateClient();

        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/auth/status")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/app.js")).StatusCode);
    }

    [TestMethod]
    public async Task NoPathBase_RootServesNormally()
    {
        using var factory = TestApp.CreateFactory(_dataDir!, webRoot: WwwRoot());
        using var client = factory.CreateClient();

        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/app.js")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/api/auth/status")).StatusCode);
    }
}
