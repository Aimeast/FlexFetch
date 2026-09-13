using FlexFetch.Entities;
using FlexFetch.Services.Downloaders;

namespace FlexFetch.Tests;

[TestClass]
public sealed class CookieFileTests
{
    [TestMethod]
    public void RoundTrip_PreservesHttpOnlyCookies()
    {
        var cookies = new List<CookieItem>
        {
            new()
            {
                Domain = ".youtube.com",
                Name = "LOGIN_INFO",
                Value = "AFmmF2s",
                Path = "/",
                HttpOnly = true,
                Secure = true,
                ExpiresAt = DateTime.UtcNow.AddDays(90),
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"flexfetch-cookies-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, CookieFile.BuildContent(cookies));
            var read = CookieFile.Read(path);

            // The #HttpOnly_ prefix must be recognized BEFORE the generic
            // comment check: a plain StartsWith('#') silently drops the whole
            // core session family.
            Assert.HasCount(1, read);
            Assert.IsTrue(read[0].HttpOnly);
            Assert.AreEqual("LOGIN_INFO", read[0].Name);
            Assert.AreEqual("AFmmF2s", read[0].Value);
            Assert.AreEqual(".youtube.com", read[0].Domain);
            Assert.IsTrue(read[0].Secure);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void BuildContent_HostOnlyDomain_NotWidenedToSubdomains()
    {
        var cookies = new List<CookieItem>
        {
            new() { Domain = "youtube.com", Name = "HOSTONLY", Value = "v", Path = "/" },
            new() { Domain = ".youtube.com", Name = "DOMAINWIDE", Value = "v", Path = "/" },
        };

        var content = CookieFile.BuildContent(cookies);

        // Host-only (no leading dot) -> includeSubdomains FALSE; domain-wide
        // (leading dot) -> TRUE. A host-only cookie must never widen.
        StringAssert.Contains(content, "youtube.com\tFALSE\t/\tFALSE\t0\tHOSTONLY\tv\n");
        StringAssert.Contains(content, ".youtube.com\tTRUE\t/\tFALSE\t0\tDOMAINWIDE\tv\n");
    }

    [TestMethod]
    public void RoundTrip_HostOnlyDomain_StaysHostOnly()
    {
        var cookies = new List<CookieItem>
        {
            new() { Domain = "youtube.com", Name = "HOSTONLY", Value = "v", Path = "/" },
        };

        var path = Path.Combine(Path.GetTempPath(), $"flexfetch-cookies-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, CookieFile.BuildContent(cookies));
            var read = CookieFile.Read(path);

            Assert.HasCount(1, read);
            Assert.AreEqual("youtube.com", read[0].Domain);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void RoundTrip_HostPrefixCookie_PreservesName()
    {
        var cookies = new List<CookieItem>
        {
            new()
            {
                Domain = "youtube.com",
                Name = "__Host-YSC",
                Value = "v",
                Path = "/",
                Secure = true,
                HttpOnly = true,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"flexfetch-cookies-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, CookieFile.BuildContent(cookies));
            var read = CookieFile.Read(path);

            // __Host- cookies are host-only by definition; the jar line keeps
            // the plain host form so no attribute can widen it.
            Assert.HasCount(1, read);
            Assert.AreEqual("__Host-YSC", read[0].Name);
            Assert.AreEqual("youtube.com", read[0].Domain);
            Assert.IsTrue(read[0].HttpOnly);
            Assert.IsTrue(read[0].Secure);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void BuildContent_SessionCookie_UsesZeroExpiry()
    {
        var cookies = new List<CookieItem>
        {
            new() { Domain = ".youtube.com", Name = "YSC", Value = "v", Path = "/" },
        };

        var content = CookieFile.BuildContent(cookies);

        StringAssert.Contains(content, "\t0\tYSC\tv\n");
    }

    [TestMethod]
    public void RoundTrip_ExpiryTimestamp_Preserves()
    {
        var expires = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var cookies = new List<CookieItem>
        {
            new() { Domain = ".youtube.com", Name = "PREF", Value = "v", Path = "/", ExpiresAt = expires },
        };

        var path = Path.Combine(Path.GetTempPath(), $"flexfetch-cookies-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, CookieFile.BuildContent(cookies));
            var read = CookieFile.Read(path);

            Assert.IsNotNull(read[0].ExpiresAt);
            Assert.AreEqual(expires, read[0].ExpiresAt!.Value);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void Read_RealYtdlpJar_ParsesAllCoreSessionLines()
    {
        // A jar shaped like yt-dlp's own output: header comments plus the
        // HttpOnly core session family - none of those lines may be dropped.
        var jar = string.Join("\n",
            "# Netscape HTTP Cookie File",
            "# This is a generated file! Do not edit.",
            "",
            ".youtube.com\tTRUE\t/\tTRUE\t1790000000\tPREF\tpref",
            "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1790000000\tSID\tsid-value",
            "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1790000000\tHSID\thsid-value",
            "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1790000000\tSAPISID\tsapisid-value",
            "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1790000000\tLOGIN_INFO\tlogin-info");

        var path = Path.Combine(Path.GetTempPath(), $"flexfetch-cookies-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(path, jar);
            var read = CookieFile.Read(path);

            Assert.HasCount(5, read);
            Assert.AreEqual(4, read.Count(c => c.HttpOnly));
            Assert.IsTrue(read.Any(c => c.Name == "LOGIN_INFO"));
            Assert.IsTrue(read.Any(c => c.Name == "SAPISID"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
