using FlexFetch.Entities;
using FlexFetch.Services.Downloaders;

namespace FlexFetch.Tests;

[TestClass]
public sealed class CookieFileTests
{
    [TestMethod]
    public void BuildContent_EmitsSharedCookieOnBothDomains()
    {
        var cookies = new List<CookieItem>
        {
            new()
            {
                Domain = ".youtube.com",
                Name = "SID",
                Value = "abc",
                Path = "/",
                SharedDomains = new List<string> { ".google.com" },
            },
            new()
            {
                Domain = ".youtube.com",
                Name = "PREF",
                Value = "xyz",
                Path = "/",
            },
        };

        var content = CookieFile.BuildContent(cookies);

        StringAssert.Contains(content, ".youtube.com\tTRUE\t/\tFALSE\t0\tSID\tabc\n");
        StringAssert.Contains(content, ".google.com\tTRUE\t/\tFALSE\t0\tSID\tabc\n");
        // PREF is not shared: exactly one line.
        Assert.AreEqual(1, content.Split('\n').Count(l => l.Contains("\tPREF\t")));
    }

    [TestMethod]
    public void BuildContent_EmitsHttpOnlyPrefixOnBothDomains()
    {
        var cookies = new List<CookieItem>
        {
            new()
            {
                Domain = ".youtube.com",
                Name = "__Secure-1PSID",
                Value = "abc",
                Path = "/",
                HttpOnly = true,
                Secure = true,
                SharedDomains = new List<string> { ".google.com" },
            },
        };

        var content = CookieFile.BuildContent(cookies);

        StringAssert.Contains(content, "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t0\t__Secure-1PSID\tabc\n");
        StringAssert.Contains(content, "#HttpOnly_.google.com\tTRUE\t/\tTRUE\t0\t__Secure-1PSID\tabc\n");
    }
}
