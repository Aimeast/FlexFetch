using System.Net;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Session;

namespace FlexFetch.Tests;

[TestClass]
public sealed class TwitterSessionServiceTests
{
    private readonly string _dir = TestApp.CreateTempDataDir();

    private static HttpResponseMessage IdentityResponse(string account = "someone") => new(HttpStatusCode.OK)
    {
        Content = new StringContent($"<html><body>{{\"screen_name\":\"{account}\"}}</body></html>"),
    };

    /// <summary>A response carrying the live-identity cookies the way x.com
    /// sets them for a presented auth_token.</summary>
    private static HttpResponseMessage IdentityResponseWithCookies(string account = "someone")
    {
        var response = IdentityResponse(account);
        response.Headers.Add("Set-Cookie", "ct0=fresh-csrf; Path=/; Secure");
        response.Headers.Add("Set-Cookie", "twid=u%3D190522811; Path=/; Secure");
        return response;
    }

    /// <summary>A 200 response that does NOT set ct0/twid: how x.com answers
    /// a revoked or anonymous token.</summary>
    private static HttpResponseMessage AnonymousResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("<html><body>anonymous shell</body></html>"),
    };

    private static (TwitterSessionStore Store, TwitterSessionService Service, string DataDir) Create(
        Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<HttpResponseMessage>>? send = null)
    {
        var dataDir = TestApp.CreateTempDataDir();
        var storage = new StorageService(dataDir);
        var files = new SessionFileStore(storage, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), TestLog.Instance);
        var store = new TwitterSessionStore(files);
        var service = new TwitterSessionService(store, new DirectProxyService(), TestLog.Instance, send);
        return (store, service, dataDir);
    }

    private static string NetscapeJar(params (string Name, string Value)[] cookies) =>
        string.Join("\n", cookies.Select(c => $".x.com\tTRUE\t/\tTRUE\t0\t{c.Name}\t{c.Value}"));

    [TestMethod]
    public async Task ImportAsync_AcceptsLiveToken_WritesEncryptedFileWithFreshTickets()
    {
        string? presentedCookie = null;
        var (store, service, dataDir) = Create((url, headers, ct) =>
        {
            presentedCookie = headers!["Cookie"];
            return Task.FromResult(IdentityResponseWithCookies());
        });

        var result = await service.ImportAsync(NetscapeJar(("auth_token", "pasted-token"), ("kdt", "legacy")));

        Assert.IsTrue(result.Ok, result.Error);
        Assert.AreEqual("auth_token=pasted-token", presentedCookie, "the check must present the pasted auth_token");
        Assert.AreEqual("someone", result.Account);
        Assert.IsTrue(store.Exists);

        var cookies = store.ReadCookies();
        Assert.IsTrue(cookies.Any(c => c.Name == "auth_token" && c.Value == "pasted-token"));
        Assert.IsTrue(cookies.Any(c => c.Name == "ct0" && c.Value == "fresh-csrf"), "the refreshed ct0 must be stored");

        var meta = store.ReadMeta();
        Assert.IsNotNull(meta);
        Assert.AreEqual("190522811", meta.UserId);
        Assert.AreEqual(TwitterSessionHealth.Ok.ToString(), meta.Health);
        Assert.IsNotNull(meta.ImportedAt);

        // The file on disk must not leak the token or metadata in plaintext.
        var raw = File.ReadAllText(Path.Combine(dataDir, "session", "twitter.session"));
        Assert.IsFalse(raw.Contains("pasted-token"), "the auth_token must not appear in the file");
        Assert.IsFalse(raw.Contains("someone"), "the account must not appear in the file");
    }

    [TestMethod]
    public async Task ImportAsync_RejectsJarWithoutAuthToken()
    {
        var (store, service, _) = Create((url, headers, ct) => Task.FromResult(IdentityResponseWithCookies()));

        var result = await service.ImportAsync(NetscapeJar(("ct0", "anonymous-csrf")));

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Error, "auth_token");
        Assert.IsFalse(store.Exists, "a rejected import must not write the session file");
    }

    [TestMethod]
    public void UpdateMeta_DoesNotResurrectAClearedSession()
    {
        var (store, _, _) = Create();
        store.Write(
            new List<CookieItem> { new() { Domain = ".x.com", Name = "auth_token", Value = "t" } },
            new TwitterSessionMeta());
        store.Delete();

        // The downloader records auth-failure verdicts from concurrent
        // download tasks; racing a session clearance must not bring the
        // file back as an empty jar.
        store.UpdateMeta(m => m.Health = TwitterSessionHealth.Invalid.ToString());

        Assert.IsFalse(store.Exists);
        Assert.IsNull(store.ReadMeta());
    }

    [TestMethod]
    public async Task ImportAsync_RejectsJarWithoutTwitterDomains()
    {
        var (_, service, _) = Create((url, headers, ct) => Task.FromResult(IdentityResponseWithCookies()));

        var result = await service.ImportAsync(".example.com\tTRUE\t/\tTRUE\t0\tA\tB");

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Error, "x.com");
    }

    [TestMethod]
    public async Task ImportAsync_RejectsRevokedToken_KeepsPreviousSession()
    {
        var (store, service, _) = Create((url, headers, ct) =>
        {
            var accepted = headers!["Cookie"].Contains("good-token", StringComparison.Ordinal);
            return Task.FromResult(accepted ? IdentityResponseWithCookies() : AnonymousResponse());
        });

        var first = await service.ImportAsync(NetscapeJar(("auth_token", "good-token")));
        Assert.IsTrue(first.Ok);

        // Second import: x.com no longer sets ct0/twid - revoked token.
        var second = await service.ImportAsync(NetscapeJar(("auth_token", "revoked-token")));

        Assert.IsFalse(second.Ok);
        Assert.AreEqual(TwitterSessionHealth.Invalid, second.Health);
        // The previously accepted session is NOT overwritten by the failure.
        var meta = store.ReadMeta();
        Assert.IsNotNull(meta);
        Assert.AreEqual(TwitterSessionHealth.Ok.ToString(), meta.Health, "a rejected import must not flip the stored verdict");
        Assert.IsTrue(store.ReadCookies().Any(c => c.Name == "auth_token" && c.Value == "good-token"));
    }

    [TestMethod]
    public async Task CheckAsync_RefreshesVerdictBothWays()
    {
        var revoked = false;
        var (store, service, _) = Create((url, headers, ct) =>
            Task.FromResult(revoked ? AnonymousResponse() : IdentityResponseWithCookies()));

        await service.ImportAsync(NetscapeJar(("auth_token", "good-token")));

        // Happy path: CheckAsync reports Ok.
        var ok = await service.CheckAsync();
        Assert.AreEqual(TwitterSessionHealth.Ok, ok);

        // The token gets revoked server-side afterwards.
        revoked = true;
        var bad = await service.CheckAsync();
        Assert.AreEqual(TwitterSessionHealth.Invalid, bad);

        var meta = store.ReadMeta();
        Assert.IsNotNull(meta);
        Assert.AreEqual(TwitterSessionHealth.Invalid.ToString(), meta.Health);
        StringAssert.Contains(meta.LastError, "ct0/twid");
        Assert.IsNotNull(meta.LastCheckAt);
    }

    [TestMethod]
    public async Task CheckAsync_WithoutSession_ReturnsUnknown()
    {
        var (_, service, _) = Create();
        var health = await service.CheckAsync();
        Assert.AreEqual(TwitterSessionHealth.Unknown, health);
    }

    [TestMethod]
    public void Delete_RemovesTheSessionFile()
    {
        var storage = new StorageService(_dir);
        var files = new SessionFileStore(storage, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), TestLog.Instance);
        var store = new TwitterSessionStore(files);
        store.Write(new List<CookieItem> { new() { Domain = ".x.com", Name = "auth_token", Value = "t" } },
            new TwitterSessionMeta { Account = "a" });

        store.Delete();

        Assert.IsFalse(store.Exists);
        Assert.IsEmpty(store.ReadCookies());
    }

    [TestMethod]
    public void FilterTwitterDomain_KeepsOnlyXAndTwitterDomains()
    {
        var cookies = new List<CookieItem>
        {
            new() { Domain = ".x.com", Name = "auth_token", Value = "1" },
            new() { Domain = "x.com", Name = "ct0", Value = "2" },
            new() { Domain = ".twitter.com", Name = "kdt", Value = "3" },
            new() { Domain = ".twimg.com", Name = "cdn", Value = "4" },
            new() { Domain = ".youtube.com", Name = "SID", Value = "5" },
        };

        var filtered = TwitterSessionStore.FilterTwitterDomain(cookies);

        Assert.HasCount(3, filtered);
        Assert.IsFalse(filtered.Any(c => c.Domain.Contains("twimg", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Merge_IncomingWinsPerKeyAndExpiredDrop()
    {
        var existing = new List<CookieItem>
        {
            new() { Domain = ".x.com", Name = "ct0", Value = "old" },
            new() { Domain = ".x.com", Name = "auth_token", Value = "keep", ExpiresAt = DateTime.UtcNow.AddHours(-1) },
        };
        var incoming = new List<CookieItem>
        {
            new() { Domain = ".x.com", Name = "ct0", Value = "new" },
        };

        var merged = TwitterSessionStore.Merge(existing, incoming);

        Assert.HasCount(1, merged, "the expired auth_token must drop, ct0 must be replaced");
        Assert.AreEqual("new", merged[0].Value);
    }

    [TestMethod]
    public async Task ImportAsync_MirrorsIdentityCookiesToTheSiblingDomain()
    {
        var (store, service, _) = Create((url, headers, ct) => Task.FromResult(IdentityResponseWithCookies()));

        // An x.com-only import must also enable the session for twitter.com
        // links (both hostnames are live; old posts still carry it).
        var result = await service.ImportAsync(NetscapeJar(("auth_token", "t0ken")));

        Assert.IsTrue(result.Ok, result.Error);
        var cookies = store.ReadCookies();
        Assert.IsTrue(cookies.Any(c => c.Name == "auth_token" && c.Domain == ".x.com"));
        Assert.IsTrue(cookies.Any(c => c.Name == "auth_token" && c.Domain == ".twitter.com"),
            "the auth_token must be cloned to the twitter.com domain");
        Assert.IsTrue(cookies.Any(c => c.Name == "ct0" && c.Domain == ".twitter.com"));
    }

    [TestMethod]
    public async Task ImportAsync_TwitterComJar_MirrorsToXCom()
    {
        var (store, service, _) = Create((url, headers, ct) => Task.FromResult(IdentityResponseWithCookies()));

        var result = await service.ImportAsync(NetscapeJar(("auth_token", "t0ken")).Replace(".x.com", ".twitter.com"));

        Assert.IsTrue(result.Ok, result.Error);
        var cookies = store.ReadCookies();
        Assert.IsTrue(cookies.Any(c => c.Name == "auth_token" && c.Domain == ".twitter.com"));
        Assert.IsTrue(cookies.Any(c => c.Name == "auth_token" && c.Domain == ".x.com"),
            "an old twitter.com jar must be mirrored to the x.com domain");
    }

    [TestMethod]
    public void MirrorSiblingDomain_KeepsBothDomainImportsAndSubdomainScope()
    {
        var cookies = new List<CookieItem>
        {
            new() { Domain = ".x.com", Name = "auth_token", Value = "v" },
            new() { Domain = ".twitter.com", Name = "auth_token", Value = "v" },
            new() { Domain = ".mobile.x.com", Name = "side", Value = "v" },
        };

        var mirrored = TwitterSessionStore.MirrorSiblingDomain(cookies);

        // Both domains already present -> unchanged; the subdomain-scoped
        // cookie is not widened.
        Assert.HasCount(3, mirrored);
        Assert.AreEqual(".mobile.x.com", mirrored.Single(c => c.Name == "side").Domain);
    }
}
