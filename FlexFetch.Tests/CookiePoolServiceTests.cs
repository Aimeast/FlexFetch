using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Enums;
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
        // The YouTube plugin carries the site's cross-domain mapping; the
        // tests use a lightweight stand-in with the same sibling/identity
        // behavior (Google identity cookies shared between .youtube.com and
        // .google.com).
        _pool = new CookiePoolService(new CookieRepository(_store), () => new ICookieDomainMapping[] { new TestYouTubeMapping(), new DefaultCookieDomainMapping() });
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

    // --- Cross-domain cookie sharing (SharedDomains) ---

    [TestMethod]
    public void ImportItems_SetsSharedDomainsOnSharedCookie()
    {
        var result = _pool!.ImportItems(new[] { Cookie(".youtube.com", "SID", "abc") }, "youtube.com");

        Assert.AreEqual(1, result.Imported);
        var group = _pool.GetGroups().Single();
        var sid = group.Cookies.Single(c => c.Name == "SID");
        CollectionAssert.Contains(sid.SharedDomains!.ToArray(), ".google.com");
    }

    [TestMethod]
    public void ImportItems_DoesNotAnnotateSiteSpecificCookies()
    {
        _pool!.ImportItems(new[] { Cookie(".youtube.com", "PREF", "abc") }, "youtube.com");

        var group = _pool.GetGroups().Single();
        var pref = group.Cookies.Single(c => c.Name == "PREF");
        Assert.IsNull(pref.SharedDomains);
    }

    [TestMethod]
    public void GetCookiesForUrl_MatchesSharedSiblingDomain()
    {
        _pool!.ImportItems(new[] { Cookie(".youtube.com", "SID", "abc") }, "youtube.com");

        var forGoogle = _pool.GetCookiesForUrl(new Uri("https://accounts.google.com/"));
        var forYoutube = _pool.GetCookiesForUrl(new Uri("https://www.youtube.com/"));

        Assert.HasCount(1, forGoogle);
        Assert.AreEqual("SID", forGoogle[0].Name);
        Assert.HasCount(1, forYoutube);
    }

    [TestMethod]
    public void UpsertCookies_SiblingEntryUpdatesSharedOwner()
    {
        _pool!.ImportItems(new[] { Cookie(".youtube.com", "SID", "old") }, "youtube.com");

        _pool.UpsertCookies(new[] { Cookie(".google.com", "SID", "new") });

        // No stray group is created for .google.com: the sibling entry updates
        // the single stored shared cookie.
        var groups = _pool.GetGroups();
        Assert.HasCount(1, groups);
        var sid = groups[0].Cookies.Single(c => c.Name == "SID");
        Assert.AreEqual("new", sid.Value);
        CollectionAssert.Contains(sid.SharedDomains!.ToArray(), ".google.com");
    }

    [TestMethod]
    public void SyncGroupCookies_KeepsSharedCookieWhenSiblingSurvives()
    {
        _pool!.ImportItems(new[] { Cookie(".youtube.com", "SID", "abc") }, "youtube.com");
        var group = _pool.GetGroups().Single();
        var before = group.Cookies.ToList();

        // The export only carries the sibling-domain entry: the shared cookie
        // is the same identity and must survive the sync-delete.
        var exported = new List<CookieItem> { Cookie(".google.com", "SID", "abc") };
        var removed = _pool.SyncGroupCookies(group.Id, before, exported);

        Assert.AreEqual(0, removed);
        Assert.HasCount(1, _pool.GetGroups().Single().Cookies);
    }

    [TestMethod]
    public void SyncGroupCookies_RemovesVanishedCookie()
    {
        _pool!.ImportItems(new[] { Cookie(".youtube.com", "PREF", "abc") }, "youtube.com");
        var group = _pool.GetGroups().Single();
        var before = group.Cookies.ToList();

        var removed = _pool.SyncGroupCookies(group.Id, before, new List<CookieItem>());

        Assert.AreEqual(1, removed);
        Assert.IsEmpty(_pool.GetGroups().Single().Cookies);
    }

    private static CookieItem Cookie(string domain, string name, string value = "v", string path = "/", DateTime? expiresAt = null, bool secure = false) =>
        new() { Domain = domain, Path = path, Name = name, Value = value, ExpiresAt = expiresAt, Secure = secure };

    [TestMethod]
    public void ImportJson_ParsesArrayFormat()
    {
        const string json =
            "[{\"domain\":\".youtube.com\",\"name\":\"SID\",\"value\":\"abc\",\"path\":\"/\",\"secure\":true,\"httpOnly\":true,\"expirationDate\":4102444800}]";

        var result = _pool!.ImportJson(json);

        Assert.AreEqual(1, result.Imported);
        var header = _pool.GetCookieHeader(new Uri("https://www.youtube.com/watch?v=1"));
        StringAssert.Contains(header, "SID=abc");
    }

    [TestMethod]
    public void ImportJson_ParsesWrappedCookiesObject()
    {
        const string json =
            "{\"cookies\":[{\"domain\":\".google.com\",\"name\":\"HSID\",\"value\":\"xyz\"}]}";

        var result = _pool!.ImportJson(json);

        Assert.AreEqual(1, result.Imported);
        StringAssert.Contains(_pool!.GetCookieHeader(new Uri("https://www.google.com/")), "HSID=xyz");
    }

    [TestMethod]
    public void ImportJson_SessionCookie_HasNoExpiry()
    {
        const string json =
            "[{\"domain\":\".youtube.com\",\"name\":\"SID\",\"value\":\"abc\",\"session\":true}]";

        _pool!.ImportJson(json);

        var cookies = _pool.GetCookiesForUrl(new Uri("https://www.youtube.com/watch?v=1"));
        Assert.IsNull(cookies.Single().ExpiresAt);
    }

    [TestMethod]
    public void ImportJson_InvalidJson_ReportsError()
    {
        var result = _pool!.ImportJson("{ not json");

        Assert.AreEqual(0, result.Imported);
        Assert.IsNotEmpty(result.Errors);
    }

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
    public void ImportText_NullUrl_ParsesJson()
    {
        const string json =
            "[{\"domain\":\".youtube.com\",\"name\":\"SID\",\"value\":\"abc\",\"path\":\"/\",\"secure\":true,\"httpOnly\":true,\"expirationDate\":4102444800}]";

        var result = _pool!.ImportText(url: null, json);

        Assert.AreEqual(1, result.Imported);
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://www.youtube.com/watch?v=1")), "SID=abc");
    }

    [TestMethod]
    public void ImportText_NullUrl_ParsesNetscape()
    {
        var content = ".example.com\tTRUE\t/\tFALSE\t4102444800\tA\t1";

        var result = _pool!.ImportText(url: null, content);

        Assert.AreEqual(1, result.Imported);
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://example.com/")), "A=1");
    }

    [TestMethod]
    public void ImportText_NullUrl_SetCookie_ReportsError()
    {
        var result = _pool!.ImportText(url: null, "token=t; Path=/; Domain=example.com");

        Assert.AreEqual(0, result.Imported);
        Assert.IsNotEmpty(result.Errors);
    }

    [TestMethod]
    public void ImportItems_ImportsEditedList()
    {
        var edited = new[]
        {
            new CookieItem { Domain = ".youtube.com", Name = "SID", Value = "new", Path = "/", Secure = true, HttpOnly = true },
            new CookieItem { Domain = ".twitter.com", Name = "TWID", Value = "x" },
        };

        var result = _pool!.ImportItems(edited);

        Assert.AreEqual(2, result.Imported);
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://www.youtube.com/")), "SID=new");
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://twitter.com/")), "TWID=x");
    }

    [TestMethod]
    public void ImportItems_RespectsGroupAndUpserts()
    {
        _pool!.ImportNetscape(".youtube.com\tTRUE\t/\tFALSE\t4102444800\tSID\told", "youtube");

        var edited = new[]
        {
            new CookieItem { Domain = ".youtube.com", Name = "SID", Value = "new" },
        };

        _pool.ImportItems(edited, "youtube");

        var group = _pool.GetGroups().Single(g => g.Name == "youtube");
        Assert.HasCount(1, group.Cookies);
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://www.youtube.com/")), "SID=new");
    }

    [TestMethod]
    public void Import_RejectsDifferentDomainInSameGroup()
    {
        _pool!.ImportNetscape(".youtube.com\tTRUE\t/\tFALSE\t4102444800\tSID\t1", "youtube");

        var result = _pool.ImportItems(
            new[] { new CookieItem { Domain = ".google.com", Name = "HSID", Value = "x" } },
            "youtube");

        Assert.AreEqual(0, result.Imported);
        Assert.IsNotEmpty(result.Errors);
        // The group keeps only its original domain.
        var group = _pool.GetGroups().Single(g => g.Name == "youtube");
        Assert.HasCount(1, group.Cookies);
        Assert.AreEqual(".youtube.com", group.Cookies[0].Domain);
    }

    [TestMethod]
    public void Import_WithoutGroupName_GroupsByDomain()
    {
        _pool!.ImportNetscape(".youtube.com\tTRUE\t/\tFALSE\t4102444800\tSID\t1\n");
        _pool.ImportNetscape(".twitter.com\tTRUE\t/\tFALSE\t4102444800\tTWID\t2\n");

        var groups = _pool.GetGroups();
        Assert.HasCount(2, groups);
        foreach (var group in groups)
        {
            Assert.HasCount(1, group.Cookies.Select(c => c.Domain.ToLowerInvariant()).Distinct());
        }
    }

    [TestMethod]
    public void Import_SameDomain_DifferentSpelling_ReusesGroup()
    {
        // First import uses the leading-dot form; the second uses a bare,
        // differently-cased domain. Both must land in the same group.
        _pool!.ImportNetscape(".youtube.com\tTRUE\t/\tFALSE\t4102444800\tSID\t1\n");
        _pool.ImportNetscape("YouTube.com\tTRUE\t/\tFALSE\t4102444800\tHSID\t2\n");

        var groups = _pool.GetGroups();
        Assert.HasCount(1, groups);
        Assert.HasCount(2, groups[0].Cookies);
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://www.youtube.com/")), "SID=1; HSID=2");
    }

    [TestMethod]
    public void Import_SameDomain_UpdatesExistingGroupInsteadOfNew()
    {
        _pool!.ImportJson(
            "[{\"domain\":\".youtube.com\",\"name\":\"SID\",\"value\":\"old\"}]");

        // Parse/import the same domain JSON again: should update, not add a group.
        var result = _pool.ImportJson(
            "[{\"domain\":\".youtube.com\",\"name\":\"SID\",\"value\":\"new\"}]");

        Assert.AreEqual(1, result.Imported);
        var groups = _pool.GetGroups();
        Assert.HasCount(1, groups);
        Assert.HasCount(1, groups[0].Cookies);
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://www.youtube.com/")), "SID=new");
    }

    [TestMethod]
    public void Import_SameCookie_UpdatesValueInsteadOfDuplicating()
    {
        _pool!.ImportNetscape(".youtube.com\tTRUE\t/\tFALSE\t4102444800\tSID\told\n");

        var result = _pool.ImportNetscape(".youtube.com\tTRUE\t/\tFALSE\t4102444800\tSID\tnew\n");

        Assert.AreEqual(1, result.Imported);
        var group = _pool.GetGroups().Single();
        Assert.HasCount(1, group.Cookies);
        Assert.AreEqual("new", group.Cookies[0].Value);
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://www.youtube.com/")), "SID=new");
    }

    [TestMethod]
    public void RemoveCookie_RemovesSingleCookieByKey()
    {
        _pool!.ImportNetscape(
            ".youtube.com\tTRUE\t/\tFALSE\t4102444800\tSID\t1\n"
            + ".youtube.com\tTRUE\t/\tFALSE\t4102444800\tHSID\t2\n");

        var ok = _pool.RemoveCookie("youtube.com", ".youtube.com", "/", "SID");

        Assert.IsTrue(ok);
        Assert.DoesNotContain(_pool.GetCookieHeader(new Uri("https://www.youtube.com/")), "SID=");
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://www.youtube.com/")), "HSID=2");
    }

    [TestMethod]
    public void RemoveCookie_FindsGroupByDomain_WhenGroupNameIsNull()
    {
        _pool!.ImportNetscape(".youtube.com\tTRUE\t/\tFALSE\t4102444800\tSID\t1\n");

        Assert.IsTrue(_pool.RemoveCookie(null, ".youtube.com", "/", "SID"));
        Assert.AreEqual(string.Empty, _pool.GetCookieHeader(new Uri("https://www.youtube.com/")));
    }

    [TestMethod]
    public void RemoveCookie_NoMatch_ReturnsFalse()
    {
        _pool!.ImportNetscape(".youtube.com\tTRUE\t/\tFALSE\t4102444800\tSID\t1\n");

        Assert.IsFalse(_pool.RemoveCookie("youtube.com", ".youtube.com", "/", "missing"));
        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://www.youtube.com/")), "SID=1");
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
    public void UpdateGroupUrls_ReplacesUrls()
    {
        var group = _pool!.CreateGroup("youtube", new[] { "https://www.youtube.com" });
        Assert.HasCount(1, _pool.GetGroups().Single(g => g.Id == group.Id).Urls);

        Assert.IsTrue(_pool.UpdateGroupUrls(group.Id, new[]
        {
            "  https://www.youtube.com/feed/trending  ",
            "https://www.youtube.com/watch?v=abc",
            "https://www.youtube.com",
        }));

        var urls = _pool.GetGroups().Single(g => g.Id == group.Id).Urls;
        // Trimmed, blank filtered, deduplicated case-insensitively.
        Assert.HasCount(3, urls);
        Assert.AreEqual("https://www.youtube.com/feed/trending", urls[0]);
        Assert.AreEqual("https://www.youtube.com/watch?v=abc", urls[1]);
        Assert.AreEqual("https://www.youtube.com", urls[2]);

        // Clearing the list empties the group's refresh targets.
        Assert.IsTrue(_pool.UpdateGroupUrls(group.Id, Array.Empty<string>()));
        Assert.HasCount(0, _pool.GetGroups().Single(g => g.Id == group.Id).Urls);

        // Unknown group id -> false.
        Assert.IsFalse(_pool.UpdateGroupUrls("missing", new[] { "https://x.com" }));
    }

    [TestMethod]
    public void RefreshStatus_TransitionsRunningOkFailed()
    {
        var group = _pool!.CreateGroup("youtube", new[] { "https://www.youtube.com" });

        // Running: error cleared, no timestamp yet.
        Assert.IsTrue(_pool.MarkGroupRefreshing(group.Id));
        var running = _pool.GetGroups().Single(g => g.Id == group.Id);
        Assert.AreEqual(CookieRefreshStatus.Running, running.LastRefreshStatus);
        Assert.IsNull(running.LastRefreshError);
        Assert.IsNull(running.LastRefreshedAt);

        // Ok: status + timestamp recorded.
        Assert.IsTrue(_pool.MarkGroupRefreshed(group.Id));
        var ok = _pool.GetGroups().Single(g => g.Id == group.Id);
        Assert.AreEqual(CookieRefreshStatus.Ok, ok.LastRefreshStatus);
        Assert.IsNotNull(ok.LastRefreshedAt);
        Assert.IsNull(ok.LastRefreshError);

        // Failed: status + error recorded, timestamp unchanged from Ok.
        Assert.IsTrue(_pool.MarkGroupRefreshFailed(group.Id, "boom"));
        var failed = _pool.GetGroups().Single(g => g.Id == group.Id);
        Assert.AreEqual(CookieRefreshStatus.Failed, failed.LastRefreshStatus);
        Assert.AreEqual("boom", failed.LastRefreshError);
        Assert.AreEqual(ok.LastRefreshedAt, failed.LastRefreshedAt);

        // Unknown id -> false.
        Assert.IsFalse(_pool.MarkGroupRefreshing("missing"));
        Assert.IsFalse(_pool.MarkGroupRefreshed("missing"));
        Assert.IsFalse(_pool.MarkGroupRefreshFailed("missing", "x"));
    }

    [TestMethod]
    public void RefreshGate_SerializesConcurrentRuns()
    {
        // First acquisition succeeds; a second concurrent one is rejected.
        Assert.IsTrue(_pool!.TryStartRefresh());
        Assert.IsTrue(_pool.IsRefreshing);
        Assert.IsFalse(_pool.TryStartRefresh());

        // After EndRefresh the gate is free again.
        _pool.EndRefresh();
        Assert.IsFalse(_pool.IsRefreshing);
        Assert.IsTrue(_pool.TryStartRefresh());
        _pool.EndRefresh();
    }

    [TestMethod]
    public void UpsertCookies_ReflowsIntoPool()
    {
        _pool!.ImportNetscape(".a.com\tTRUE\t/\tFALSE\t4102444800\tA\told\n");

        _pool.UpsertCookies(new[] { Cookie(".a.com", "A", "new") });

        StringAssert.Contains(_pool.GetCookieHeader(new Uri("https://a.com/")), "A=new");
    }

    [TestMethod]
    public void SyncGroupCookies_RemovesSiteDeletedAndExpired_KeepsConcurrentAndPresent()
    {
        // Pool starts with: A (site keeps it), B (site deletes it),
        // C (already expired - browsers never export expired cookies).
        _pool!.UpsertCookies(new[]
        {
            Cookie(".a.com", "A", "1"),
            Cookie(".a.com", "B", "2"),
            Cookie(".a.com", "C", "3", expiresAt: DateTime.UtcNow.AddDays(-1)),
        });
        var group = _pool.GetGroups().Single();
        var before = group.Cookies.ToList();

        // A concurrent task writes D after the snapshot was taken.
        _pool.UpsertCookies(new[] { Cookie(".a.com", "D", "4") });

        // The browser exports only A (B was removed by the site, C expired,
        // D was not part of this refresh at all).
        var removed = _pool.SyncGroupCookies(group.Id, before, new[] { Cookie(".a.com", "A", "new") });

        Assert.AreEqual(2, removed); // B and C removed
        var remaining = _pool.GetGroups().Single().Cookies;
        Assert.HasCount(2, remaining);
        Assert.IsTrue(remaining.Any(c => c.Name == "A")); // kept (still exported)
        Assert.IsTrue(remaining.Any(c => c.Name == "D")); // kept (concurrent write, not in snapshot)
        Assert.IsFalse(remaining.Any(c => c.Name is "B" or "C")); // removed

        // Unknown group id -> nothing removed.
        Assert.AreEqual(0, _pool.SyncGroupCookies("missing", before, Array.Empty<CookieItem>()));
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

    /// <summary>
    /// Stand-in for the YouTube plugin's cross-domain mapping: Google identity
    /// cookies (SID family) are shared between .youtube.com and .google.com.
    /// </summary>
    private sealed class TestYouTubeMapping : ICookieDomainMapping
    {
        public bool IsMatch(CookieGroup group)
        {
            var name = Normalize(group.Name);
            return IsSibling(name)
                || group.Cookies.Any(c => IsSibling(Normalize(c.Domain)));
        }

        public IReadOnlyList<string> GetSharedDomains(string? domain, string name)
        {
            var normalized = Normalize(domain);
            if (string.IsNullOrEmpty(normalized) || !IsIdentityCookie(name) || !IsSibling(normalized))
            {
                return Array.Empty<string>();
            }

            return normalized == "youtube.com" ? new[] { ".google.com" } : new[] { ".youtube.com" };
        }

        private static bool IsSibling(string? domain) =>
            domain is "youtube.com" or "google.com";

        private static bool IsIdentityCookie(string name) =>
            name.Contains("SID", StringComparison.OrdinalIgnoreCase)
            || name.Contains("APISID", StringComparison.OrdinalIgnoreCase)
            || name.Contains("LOGIN_INFO", StringComparison.OrdinalIgnoreCase);

        private static string Normalize(string? domain) =>
            (domain ?? string.Empty).TrimStart('.').ToLowerInvariant();
    }
}
