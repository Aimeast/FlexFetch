using FlexFetch.Data;
using FlexFetch.Domain;
using FlexFetch.Services;

namespace FlexFetch.Tests;

[TestClass]
public sealed class CookiePoolServiceTests
{
    private string? _dir;
    private LiteDbStore? _store;
    private CookiePoolService? _pool;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "flexfetch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new LiteDbStore(Path.Combine(_dir, "flexfetch.db"));
        _pool = new CookiePoolService(new CookieRepository(_store));
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

    private static CookieItem Cookie(string domain, string name, string value = "v", string path = "/", DateTime? expiresAt = null, bool secure = false) =>
        new() { Domain = domain, Path = path, Name = name, Value = value, ExpiresAt = expiresAt, Secure = secure };

    [TestMethod]
    public void ImportNetscape_ParsesAndMatchesDomain()
    {
        var content = """
        # Netscape HTTP Cookie File
        .youtube.com	TRUE	/	FALSE	4102444800	SID	abc123
        #HttpOnly_.google.com	TRUE	/	TRUE	0	HSID	xyz789
        """;

        var result = _pool!.ImportNetscape(content);

        Assert.AreEqual(2, result.Imported);
        Assert.HasCount(0, result.Errors);

        var header = _pool.GetCookieHeader(new Uri("https://www.youtube.com/watch?v=1"));
        StringAssert.Contains(header, "SID=abc123");
        // HttpOnly cookie with session expiry (0) still matches on the right domain.
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://www.google.com/")), "HSID=xyz789");
    }

    [TestMethod]
    public void ImportNetscape_SkipsCommentsAndBadLines()
    {
        var content = """
        # comment
        bad-line-without-tabs
        .example.com	TRUE	/	FALSE	4102444800	A	1
        """;

        var result = _pool!.ImportNetscape(content);

        Assert.AreEqual(1, result.Imported);
        Assert.IsGreaterThan(0, result.Errors.Count);
    }

    [TestMethod]
    public void GetCookiesForUrl_MatchesSubdomainsOnly()
    {
        _pool!.ImportNetscape(".youtube.com\tTRUE\t/\tFALSE\t4102444800\tSID\tabc\n");

        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://www.youtube.com/watch?v=1")), "SID=abc");
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://youtube.com/")), "SID=abc");

        // Cross-site: another domain must NOT receive the cookie.
        Assert.AreEqual(string.Empty, _pool.GetCookieHeader(new Uri("https://example.com/")));
    }

    [TestMethod]
    public void GetCookiesForUrl_FiltersExpired()
    {
        _pool!.ImportNetscape(
            ".a.com\tTRUE\t/\tFALSE\t4102444800\tLive\t1\n"
            + ".b.com\tTRUE\t/\tFALSE\t1\tDead\t1\n");

        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://a.com/")), "Live=1");
        Assert.AreEqual(string.Empty, _pool.GetCookieHeader(new Uri("https://b.com/")));
    }

    [TestMethod]
    public void GetCookiesForUrl_RespectsSecureFlag()
    {
        _pool!.ImportNetscape(".a.com\tTRUE\t/\tTRUE\t4102444800\tSec\t1\n");

        Assert.AreEqual(string.Empty, _pool.GetCookieHeader(new Uri("http://a.com/")));
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://a.com/")), "Sec=1");
    }

    [TestMethod]
    public void GetCookiesForUrl_RespectsPath()
    {
        _pool!.ImportNetscape(".a.com\tTRUE\t/app\tFALSE\t4102444800\tApp\t1\n");

        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://a.com/app/page")), "App=1");
        Assert.AreEqual(string.Empty, _pool.GetCookieHeader(new Uri("https://a.com/other")));
    }

    [TestMethod]
    public void ImportSetCookie_ParsesAttributes()
    {
        var result = _pool!.ImportSetCookie(
            new Uri("https://x.com/some/path"),
            new[]
            {
                "auth=token123; Path=/; Domain=x.com; Secure; HttpOnly; SameSite=Lax",
                "session=abc; Max-Age=3600",
            });

        Assert.AreEqual(2, result.Imported);

        var header = _pool.GetCookieHeader(new Uri("https://x.com/some/path"));
        StringAssert.Contains(header, "auth=token123");
        StringAssert.Contains(header, "session=abc");
    }

    [TestMethod]
    public void ImportText_AutoDetectsNetscapeVsSetCookie()
    {
        var netscape = ".example.com\tTRUE\t/\tFALSE\t4102444800\tA\t1";
        var setCookie = "token=t; Path=/; Domain=example.com";

        var r1 = _pool!.ImportText(new Uri("https://example.com/"), netscape);
        Assert.AreEqual(1, r1.Imported);

        var r2 = _pool.ImportText(new Uri("https://example.com/"), setCookie);
        Assert.AreEqual(1, r2.Imported);
    }

    [TestMethod]
    public void Groups_CrudAndIsolation()
    {
        _pool!.CreateGroup("youtube", new[] { "https://www.youtube.com" });
        _pool.ImportNetscape(".youtube.com\tTRUE\t/\tFALSE\t4102444800\tSID\t1", "youtube");
        _pool.ImportNetscape(".twitter.com\tTRUE\t/\tFALSE\t4102444800\tTWID\t2", "twitter");

        var groups = _pool.GetGroups();
        Assert.HasCount(2, groups);
        var youtube = groups.Single(g => g.Name == "youtube");
        Assert.HasCount(1, youtube.Cookies);

        // Delete the youtube group; its cookies disappear.
        Assert.IsTrue(_pool.DeleteGroup(youtube.Id));
        Assert.AreEqual(string.Empty, _pool.GetCookieHeader(new Uri("https://www.youtube.com/")));
    }

    [TestMethod]
    public void UpsertCookies_ReflowsIntoPool()
    {
        _pool!.ImportNetscape(".a.com\tTRUE\t/\tFALSE\t4102444800\tA\told\n");

        _pool.UpsertCookies(new[] { Cookie(".a.com", "A", "new") });

        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://a.com/")), "A=new");
    }

    [TestMethod]
    public void GetCookieHeader_JoinsMultipleCookies()
    {
        _pool!.ImportNetscape(
            ".a.com\tTRUE\t/\tFALSE\t4102444800\tA\t1\n"
            + ".a.com\tTRUE\t/\tFALSE\t4102444800\tB\t2\n");

        var header = _pool.GetCookieHeader(new Uri("https://a.com/"));

        StringAssert.Contains(header, "A=1");
        StringAssert.Contains(header, "B=2");
        Assert.Contains(";", header);
    }
}
