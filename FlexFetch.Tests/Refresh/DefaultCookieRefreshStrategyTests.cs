using FlexFetch.Entities;
using FlexFetch.Services.Refresh;

namespace FlexFetch.Tests.Refresh;

[TestClass]
public sealed class DefaultCookieRefreshStrategyTests
{
    private static readonly DefaultCookieRefreshStrategy Strategy = new();

    [TestMethod]
    public void IsMatch_NeverMatches()
    {
        var group = new CookieGroup { Urls = new List<string> { "https://www.youtube.com" } };

        Assert.IsFalse(Strategy.IsMatch(group));
    }

    [TestMethod]
    public void GetSessionRejectionReason_NeverRejects()
    {
        Assert.IsNull(Strategy.GetSessionRejectionReason("https://accounts.google.com/signin"));

        var before = new List<CookieItem> { new() { Domain = ".google.com", Name = "SID" } };
        var exported = new List<CookieItem>();

        Assert.IsNull(Strategy.GetSessionRejectionReason(before, exported));
    }
}
