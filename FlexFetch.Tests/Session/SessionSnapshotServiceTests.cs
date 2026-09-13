using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Session;

namespace FlexFetch.Tests;

[TestClass]
public sealed class SessionSnapshotServiceTests
{
    private readonly string _dir = TestApp.CreateTempDataDir();

    private SessionSnapshotService CreateService() =>
        new(new StorageService(_dir));

    [TestMethod]
    public void WriteAndRead_RoundTripsCookiesAndMeta()
    {
        var service = CreateService();
        Assert.IsFalse(service.Exists);

        var cookies = new List<CookieItem>
        {
            new() { Domain = ".youtube.com", Name = "SID", Value = "s", HttpOnly = true },
            new() { Domain = ".youtube.com", Name = "PREF", Value = "p" },
        };
        service.WriteSnapshot(cookies, new SessionMeta
        {
            VisitorData = "visitor-1",
            ImportedAt = DateTime.UtcNow,
        });

        Assert.IsTrue(service.Exists);
        Assert.IsNotNull(service.Age);

        var read = service.ReadCookies();
        Assert.HasCount(2, read);
        Assert.IsTrue(read.Any(c => c.HttpOnly && c.Name == "SID"));

        var meta = service.ReadMeta();
        Assert.IsNotNull(meta);
        Assert.AreEqual("visitor-1", meta.VisitorData);
        Assert.AreEqual(2, meta.CookieCount);
    }

    [TestMethod]
    public void CreateSnapshotCopy_IsIndependentOfTheSnapshot()
    {
        var service = CreateService();
        Assert.IsNull(service.CreateSnapshotCopy(), "no snapshot -> no copy");

        service.WriteSnapshot(
            new List<CookieItem> { new() { Domain = ".youtube.com", Name = "SID", Value = "s" } },
            new SessionMeta());

        var copy = service.CreateSnapshotCopy();
        Assert.IsNotNull(copy);
        Assert.AreNotEqual(Path.Combine(_dir, "session", "session.txt"), copy);

        // Mutating the copy must never touch the snapshot: yt-dlp rewrites
        // the file it is given, and a poisoned copy must not flow back.
        File.WriteAllText(copy, "poisoned");
        Assert.AreEqual("s", service.ReadCookies()[0].Value);
        File.Delete(copy);
    }

    [TestMethod]
    public void FilterYouTubeDomain_KeepsOnlyYouTubeDomains()
    {
        var cookies = new List<CookieItem>
        {
            new() { Domain = ".youtube.com", Name = "SID", Value = "1" },
            new() { Domain = "youtube.com", Name = "YSC", Value = "2" },
            new() { Domain = ".m.youtube.com", Name = "VISITOR_INFO1_LIVE", Value = "3" },
            new() { Domain = ".google.com", Name = "NID", Value = "4" },
            new() { Domain = ".example.com", Name = "a", Value = "5" },
        };

        var filtered = SessionSnapshotService.FilterYouTubeDomain(cookies);

        Assert.HasCount(3, filtered);
        Assert.IsFalse(filtered.Any(c => c.Domain.Contains("google", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Merge_MergesOnlyWithinTheSameNameAndDomainKey()
    {
        var existing = new List<CookieItem>
        {
            new() { Domain = ".youtube.com", Name = "SID", Value = "old" },
            new() { Domain = ".youtube.com", Name = "PREF", Value = "keep" },
        };
        var incoming = new List<CookieItem>
        {
            // Same key: incoming wins.
            new() { Domain = ".youtube.com", Name = "SID", Value = "new" },
            // Same name, different domain: a DIFFERENT cookie, never merged.
            new() { Domain = ".m.youtube.com", Name = "SID", Value = "mobile" },
        };

        var merged = SessionSnapshotService.Merge(existing, incoming);

        Assert.HasCount(3, merged);
        Assert.AreEqual("new", merged.Single(c => c.Domain == ".youtube.com" && c.Name == "SID").Value);
        Assert.AreEqual("keep", merged.Single(c => c.Name == "PREF").Value);
        Assert.AreEqual("mobile", merged.Single(c => c.Domain == ".m.youtube.com" && c.Name == "SID").Value);
    }

    [TestMethod]
    public void Merge_DropsExpiredCookies()
    {
        var existing = new List<CookieItem>
        {
            new() { Domain = ".youtube.com", Name = "GONE", Value = "v", ExpiresAt = DateTime.UtcNow.AddHours(-1) },
            new() { Domain = ".youtube.com", Name = "KEPT", Value = "v", ExpiresAt = DateTime.UtcNow.AddHours(1) },
        };

        var merged = SessionSnapshotService.Merge(existing, Array.Empty<CookieItem>());

        Assert.HasCount(1, merged);
        Assert.AreEqual("KEPT", merged[0].Name);
    }

    [TestMethod]
    public void MissingIdentityCookies_DetectsAnIncompleteJar()
    {
        Assert.IsEmpty(SessionExportService.MissingIdentityCookies(new List<CookieItem>
        {
            new() { Domain = ".youtube.com", Name = "LOGIN_INFO", Value = "v" },
            new() { Domain = ".youtube.com", Name = "SAPISID", Value = "v" },
            new() { Domain = ".youtube.com", Name = "APISID", Value = "v" },
            new() { Domain = ".youtube.com", Name = "__Secure-1PAPISID", Value = "v" },
            new() { Domain = ".youtube.com", Name = "__Secure-3PAPISID", Value = "v" },
        }));

        var missing = SessionExportService.MissingIdentityCookies(new List<CookieItem>
        {
            new() { Domain = ".youtube.com", Name = "PREF", Value = "v" },
        });
        Assert.HasCount(5, missing);
    }

    [TestMethod]
    public void ExtractVisitorData_TakesVisitorInfo1Live()
    {
        var jar = new List<CookieItem>
        {
            new() { Domain = ".youtube.com", Name = "PREF", Value = "x" },
            new() { Domain = ".youtube.com", Name = "VISITOR_INFO1_LIVE", Value = "visitor-value" },
        };

        Assert.AreEqual("visitor-value", SessionExportService.ExtractVisitorData(jar));
        Assert.IsNull(SessionExportService.ExtractVisitorData(
            new List<CookieItem> { new() { Domain = ".youtube.com", Name = "PREF", Value = "x" } }));
    }

    [TestMethod]
    public void MapProbeClass_MapsToHealthVerdicts()
    {
        Assert.AreEqual(SessionHealth.Ok, SessionExportService.MapProbeClass(YtdlpOutputClass.Ok));
        Assert.AreEqual(SessionHealth.BotCheck, SessionExportService.MapProbeClass(YtdlpOutputClass.BotCheck));
        Assert.AreEqual(SessionHealth.SessionRotated, SessionExportService.MapProbeClass(YtdlpOutputClass.SessionRotated));
        Assert.AreEqual(SessionHealth.JarInconsistent, SessionExportService.MapProbeClass(YtdlpOutputClass.JarInconsistent));
        Assert.AreEqual(SessionHealth.LoginRequired, SessionExportService.MapProbeClass(YtdlpOutputClass.LoginRequired));
    }
}
