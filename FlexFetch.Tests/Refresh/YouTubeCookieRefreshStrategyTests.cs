using FlexFetch.Entities;
using FlexFetch.Services.Refresh;

namespace FlexFetch.Tests.Refresh;

[TestClass]
public sealed class YouTubeCookieRefreshStrategyTests
{
    private static readonly YouTubeCookieRefreshStrategy Strategy = new();

    private static CookieItem Cookie(string domain, string name, string value = "v") =>
        new() { Domain = domain, Name = name, Value = value };

    [TestMethod]
    public void IsMatch_MatchesYoutubeUrls()
    {
        var group = new CookieGroup
        {
            Name = "YouTube",
            Urls = new List<string> { "https://www.youtube.com/watch?v=1" },
        };

        Assert.IsTrue(Strategy.IsMatch(group));
    }

    [TestMethod]
    public void IsMatch_MatchesYoutuBeUrls()
    {
        var group = new CookieGroup { Urls = new List<string> { "https://youtu.be/abc" } };

        Assert.IsTrue(Strategy.IsMatch(group));
    }

    [TestMethod]
    public void IsMatch_MatchesYoutubeCookieDomains()
    {
        var group = new CookieGroup
        {
            Urls = new List<string> { "https://example.com" },
            Cookies = new List<CookieItem> { Cookie(".youtube.com", "SID") },
        };

        Assert.IsTrue(Strategy.IsMatch(group));
    }

    [TestMethod]
    public void IsMatch_DoesNotMatchOtherSites()
    {
        var group = new CookieGroup
        {
            Urls = new List<string> { "https://x.com/SpaceX/status/1" },
            Cookies = new List<CookieItem> { Cookie(".x.com", "auth_token") },
        };

        Assert.IsFalse(Strategy.IsMatch(group));
    }

    [TestMethod]
    public void GetSessionRejectionReason_DetectsLoginAndChallengePages()
    {
        Assert.IsNotNull(Strategy.GetSessionRejectionReason("https://accounts.google.com/signin"));
        Assert.IsNotNull(Strategy.GetSessionRejectionReason("https://accounts.google.com/servicelogin"));
        Assert.IsNotNull(Strategy.GetSessionRejectionReason("https://www.google.com/sorry/index"));
        Assert.IsNotNull(Strategy.GetSessionRejectionReason("https://example.com/login"));
        Assert.IsNull(Strategy.GetSessionRejectionReason("https://www.youtube.com/"));
        Assert.IsNull(Strategy.GetSessionRejectionReason(null));
        Assert.IsNull(Strategy.GetSessionRejectionReason(""));
    }

    [TestMethod]
    public void GetSessionRejectionReason_ReturnsReasonWhenIdentityCookiesVanish()
    {
        var before = new List<CookieItem> { Cookie(".google.com", "SID"), Cookie(".google.com", "APISID") };
        var exported = new List<CookieItem> { Cookie(".youtube.com", "PREF") };

        var reason = Strategy.GetSessionRejectionReason(before, exported);

        Assert.IsNotNull(reason);
        StringAssert.Contains(reason!, "re-export");
    }

    [TestMethod]
    public void GetSessionRejectionReason_NullWhenIdentityCookiesSurvive()
    {
        var before = new List<CookieItem> { Cookie(".google.com", "SID") };
        var exported = new List<CookieItem> { Cookie(".google.com", "SID", "new-value"), Cookie(".youtube.com", "PREF") };

        Assert.IsNull(Strategy.GetSessionRejectionReason(before, exported));
    }

    [TestMethod]
    public void GetSessionRejectionReason_NullWhenNoIdentityCookiesBefore()
    {
        var before = new List<CookieItem> { Cookie(".youtube.com", "PREF") };
        var exported = new List<CookieItem> { Cookie(".youtube.com", "PREF") };

        Assert.IsNull(Strategy.GetSessionRejectionReason(before, exported));
    }
}
