using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Session;
using Microsoft.AspNetCore.DataProtection;

namespace FlexFetch.Tests;

[TestClass]
public sealed class YouTubeSessionStoreTests
{
    private readonly string _dir = TestApp.CreateTempDataDir();

    private YouTubeSessionStore CreateStore() => TestApp.CreateYouTubeStore(_dir);

    [TestMethod]
    public void WriteAndRead_RoundTripsCookiesAndMeta()
    {
        var store = CreateStore();
        Assert.IsFalse(store.Exists);

        var cookies = new List<CookieItem>
        {
            new() { Domain = ".youtube.com", Name = "SID", Value = "s", HttpOnly = true },
            new() { Domain = ".youtube.com", Name = "PREF", Value = "p" },
        };
        store.WriteSnapshot(cookies, new YouTubeSessionMeta
        {
            VisitorData = "visitor-1",
            ImportedAt = DateTime.UtcNow,
        });

        Assert.IsTrue(store.Exists);
        Assert.IsNotNull(store.Age);

        var read = store.ReadCookies();
        Assert.HasCount(2, read);
        Assert.IsTrue(read.Any(c => c.HttpOnly && c.Name == "SID"));

        var meta = store.ReadMeta();
        Assert.IsNotNull(meta);
        Assert.AreEqual("visitor-1", meta.VisitorData);
        Assert.AreEqual(2, meta.CookieCount);
    }

    [TestMethod]
    public void SessionFile_IsNotPlaintextOnDisk()
    {
        var store = CreateStore();
        store.WriteSnapshot(
            new List<CookieItem> { new() { Domain = ".youtube.com", Name = "SID", Value = "secret-value" } },
            new YouTubeSessionMeta { VisitorData = "visitor-1" });

        var raw = File.ReadAllText(Path.Combine(_dir, "session", "youtube.session"));
        Assert.IsFalse(raw.Contains("secret-value"), "the cookie value must not appear in the file");
        Assert.IsFalse(raw.Contains("visitor-1"), "the metadata must not appear in the file");
    }

    [TestMethod]
    public void Age_ComesFromMetaExportedAt_NotFromTheFileMtime()
    {
        var store = CreateStore();
        var exported = DateTime.UtcNow.AddHours(-20);
        store.WriteSnapshot(
            new List<CookieItem> { new() { Domain = ".youtube.com", Name = "SID", Value = "s" } },
            new YouTubeSessionMeta { ExportedAt = exported });

        // The file was written seconds ago; the age must still reflect the
        // export timestamp. A meta-only canary update rewrites the file and
        // must not reset the age (the mtime approach would starve the aged
        // branch of the periodic check).
        var age = store.Age;
        Assert.IsNotNull(age);
        Assert.IsTrue(age.Value.TotalHours >= 19.9, $"age {age} does not reflect ExportedAt");

        store.UpdateMeta(m => m.Health = YouTubeSessionHealth.Ok.ToString());
        var ageAfterMetaUpdate = store.Age;
        Assert.IsNotNull(ageAfterMetaUpdate);
        Assert.IsTrue(ageAfterMetaUpdate.Value.TotalHours >= 19.9, "a meta-only update must not reset the age");
    }

    [TestMethod]
    public void SiteIsolation_YoutubeFileCannotBeReadAsTwitter()
    {
        var storage = new StorageService(_dir);
        var store = new SessionFileStore(storage, new EphemeralDataProtectionProvider(), TestLog.Instance);
        store.Write("youtube", new SessionFile<YouTubeSessionMeta>
        {
            Cookies = new List<CookieItem> { new() { Domain = ".youtube.com", Name = "SID", Value = "s" } },
        });

        // Plant the youtube blob under the twitter name: the file exists, so
        // the read fails on the protector purpose, not on a missing file.
        File.Copy(store.PathFor("youtube"), store.PathFor("twitter"));

        Assert.ThrowsExactly<SessionFileUnreadableException>(
            () => store.Read<YouTubeSessionMeta>("twitter"));
    }

    [TestMethod]
    public void TamperedFile_IsRejected()
    {
        var store = CreateStore();
        store.WriteSnapshot(
            new List<CookieItem> { new() { Domain = ".youtube.com", Name = "SID", Value = "s" } },
            new YouTubeSessionMeta());

        var path = Path.Combine(_dir, "session", "youtube.session");
        var blob = File.ReadAllBytes(path);
        blob[^10] ^= 0xFF;
        File.WriteAllBytes(path, blob);

        // The unreadable file stays on disk for diagnosis; the store reads
        // as empty-but-present rather than deleting anything.
        Assert.IsTrue(store.Exists);
        Assert.IsEmpty(store.ReadCookies());
        Assert.IsNull(store.ReadMeta());
    }

    [TestMethod]
    public void UnsupportedVersion_IsRejected()
    {
        var storage = new StorageService(_dir);
        var files = new SessionFileStore(storage, new EphemeralDataProtectionProvider(), TestLog.Instance);
        var path = files.PathFor("youtube");

        // A hand-built envelope from the future: correct protector + site,
        // but a version the store must refuse to parse.
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new { version = 99, site = "youtube", cookies = Array.Empty<CookieItem>(), meta = (YouTubeSessionMeta?)null });
        File.WriteAllBytes(path, new EphemeralDataProtectionProvider()
            .CreateProtector("FlexFetch.SessionFiles.youtube").Protect(json));

        Assert.ThrowsExactly<SessionFileUnreadableException>(() => files.Read<YouTubeSessionMeta>("youtube"));
    }

    [TestMethod]
    public void MigrateLegacySnapshot_ConvertsPlaintextPairAndDeletesIt()
    {
        var sessionDir = Path.Combine(_dir, "session");
        Directory.CreateDirectory(sessionDir);
        var legacyJar = Path.Combine(sessionDir, "session.txt");
        var legacyMeta = Path.Combine(sessionDir, "session.meta.json");
        File.WriteAllText(legacyJar,
            ".youtube.com\tTRUE\t/\tTRUE\t0\tSID\tlegacy-value\n");
        File.WriteAllText(legacyMeta,
            "{\"VisitorData\":\"legacy-visitor\",\"ExportedAt\":\"2026-10-01T10:00:00Z\",\"CookieCount\":1}");

        var store = CreateStore();

        Assert.IsTrue(store.Exists, "the migrated session file must exist");
        Assert.IsFalse(File.Exists(legacyJar), "the plaintext jar must be gone after migration");
        Assert.IsFalse(File.Exists(legacyMeta), "the plaintext meta must be gone after migration");

        var cookies = store.ReadCookies();
        Assert.HasCount(1, cookies);
        Assert.AreEqual("legacy-value", cookies[0].Value);

        var meta = store.ReadMeta();
        Assert.IsNotNull(meta);
        Assert.AreEqual("legacy-visitor", meta.VisitorData);
        Assert.AreEqual(1, meta.CookieCount);
    }

    [TestMethod]
    public void MigrateLegacySnapshot_IsIdempotentAfterSuccess()
    {
        var sessionDir = Path.Combine(_dir, "session");
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "session.txt"),
            ".youtube.com\tTRUE\t/\tTRUE\t0\tSID\tv\n");
        File.WriteAllText(Path.Combine(sessionDir, "session.meta.json"), "{}");

        // Both stores share one key ring (production: one app-wide ring);
        // otherwise the second store could not decrypt the first's file.
        var storage = new StorageService(_dir);
        var files = new SessionFileStore(storage, new EphemeralDataProtectionProvider(), TestLog.Instance);
        var store = new YouTubeSessionStore(files, storage, TestLog.Instance);
        var store2 = new YouTubeSessionStore(files, storage, TestLog.Instance);

        Assert.IsTrue(store2.Exists);
        Assert.HasCount(1, store2.ReadCookies());
        Assert.IsNull(store.ReadMeta()?.VisitorData);
    }

    [TestMethod]
    public void UpdateMeta_RewritesWithoutTouchingCookies()
    {
        var store = CreateStore();
        store.WriteSnapshot(
            new List<CookieItem> { new() { Domain = ".youtube.com", Name = "SID", Value = "s" } },
            new YouTubeSessionMeta { VisitorData = "v" });

        store.UpdateMeta(m => m.Health = YouTubeSessionHealth.BotCheck.ToString());

        var meta = store.ReadMeta();
        Assert.IsNotNull(meta);
        Assert.AreEqual("BotCheck", meta.Health);
        Assert.AreEqual(1, meta.CookieCount);
        Assert.AreEqual("s", store.ReadCookies()[0].Value);
    }

    [TestMethod]
    public void CreateSnapshotCopy_IsIndependentOfTheSession()
    {
        var store = CreateStore();
        Assert.IsNull(store.CreateSnapshotCopy(), "no session -> no copy");

        store.WriteSnapshot(
            new List<CookieItem> { new() { Domain = ".youtube.com", Name = "SID", Value = "s" } },
            new YouTubeSessionMeta());

        var copy = store.CreateSnapshotCopy();
        Assert.IsNotNull(copy);
        Assert.IsTrue(copy.StartsWith(store.CopyDirectory, StringComparison.Ordinal));
        Assert.AreNotEqual(Path.Combine(_dir, "session", "youtube.session"), copy);
        // The copy is a plaintext Netscape jar (yt-dlp only reads jar files).
        Assert.IsTrue(File.ReadAllText(copy).Contains("SID"), "the copy must hold the jar as plaintext");

        // Mutating the copy must never touch the session: yt-dlp rewrites
        // the file it is given, and a poisoned copy must not flow back.
        File.WriteAllText(copy, "poisoned");
        Assert.AreEqual("s", store.ReadCookies()[0].Value);
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

        var filtered = YouTubeSessionStore.FilterYouTubeDomain(cookies);

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

        var merged = YouTubeSessionStore.Merge(existing, incoming);

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

        var merged = YouTubeSessionStore.Merge(existing, Array.Empty<CookieItem>());

        Assert.HasCount(1, merged);
        Assert.AreEqual("KEPT", merged[0].Name);
    }
}
