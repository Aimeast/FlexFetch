using FlexFetch.Entities;
using FlexFetch.Services;

namespace FlexFetch.Tests;

[TestClass]
public sealed class CookieDomainMappingTests
{
    private static CookieGroup Group(string name) => new() { Name = name };

    [TestMethod]
    public void YouTubeMapping_MatchesYoutubeAndGoogleGroups()
    {
        var mapping = new YouTubeCookieDomainMapping();

        Assert.IsTrue(mapping.IsMatch(Group("youtube.com")));
        Assert.IsTrue(mapping.IsMatch(Group("google.com")));
        Assert.IsFalse(mapping.IsMatch(Group("x.com")));
    }

    [TestMethod]
    public void YouTubeMapping_MatchesByCookieDomain()
    {
        var mapping = new YouTubeCookieDomainMapping();
        var group = Group("my session");
        group.Cookies.Add(new CookieItem { Domain = ".youtube.com", Name = "SID", Value = "v" });

        Assert.IsTrue(mapping.IsMatch(group));
    }

    [TestMethod]
    public void YouTubeMapping_GetSharedDomains_YoutubeToGoogle()
    {
        var mapping = new YouTubeCookieDomainMapping();

        CollectionAssert.Contains(mapping.GetSharedDomains(".youtube.com", "SID").ToArray(), ".google.com");
    }

    [TestMethod]
    public void YouTubeMapping_GetSharedDomains_GoogleToYoutube()
    {
        var mapping = new YouTubeCookieDomainMapping();

        CollectionAssert.Contains(mapping.GetSharedDomains(".google.com", "__Secure-1PSID").ToArray(), ".youtube.com");
    }

    [TestMethod]
    public void YouTubeMapping_GetSharedDomains_EmptyForNonSharedPairs()
    {
        var mapping = new YouTubeCookieDomainMapping();

        Assert.IsEmpty(mapping.GetSharedDomains(".youtube.com", "PREF"));
        Assert.IsEmpty(mapping.GetSharedDomains(".x.com", "SID"));
        Assert.IsEmpty(mapping.GetSharedDomains(null, "SID"));
        Assert.IsEmpty(mapping.GetSharedDomains(".youtube.com", ""));
    }

    [TestMethod]
    public void DefaultMapping_MatchesNothingAndSharesNothing()
    {
        var mapping = new DefaultCookieDomainMapping();

        Assert.IsFalse(mapping.IsMatch(Group("youtube.com")));
        Assert.IsEmpty(mapping.GetSharedDomains(".youtube.com", "SID"));
    }
}
