using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Session;

namespace FlexFetch.Tests;

[TestClass]
public sealed class YtdlpOutputClassifierTests
{
    [TestMethod]
    public void ClassifyLine_BotCheck()
    {
        Assert.AreEqual(YtdlpOutputClass.BotCheck,
            YtdlpOutputClassifier.ClassifyLine("ERROR: [youtube] abc: Sign in to confirm you're not a bot."));
        Assert.AreEqual(YtdlpOutputClass.BotCheck,
            YtdlpOutputClassifier.ClassifyLine("WARNING: Sign in to confirm that you are not a bot"));
    }

    [TestMethod]
    public void ClassifyLine_SessionRotated_EvenAsWarning()
    {
        Assert.AreEqual(YtdlpOutputClass.SessionRotated,
            YtdlpOutputClassifier.ClassifyLine("ERROR: the cookies are no longer valid"));
        Assert.AreEqual(YtdlpOutputClass.SessionRotated,
            YtdlpOutputClassifier.ClassifyLine("WARNING: [youtube] abc: cookies have been rotated; refreshing"));
    }

    [TestMethod]
    public void ClassifyLine_ClientBlocked()
    {
        Assert.AreEqual(YtdlpOutputClass.ClientBlocked,
            YtdlpOutputClassifier.ClassifyLine("ERROR: [youtube] abc: The page needs to be reloaded"));
    }

    [TestMethod]
    public void ClassifyLine_JarInconsistent()
    {
        Assert.AreEqual(YtdlpOutputClass.JarInconsistent,
            YtdlpOutputClassifier.ClassifyLine("ERROR: CookieMismatch while refreshing cookies"));
        Assert.AreEqual(YtdlpOutputClass.JarInconsistent,
            YtdlpOutputClassifier.ClassifyLine("ERROR: unable to log in: https://accounts.google.com rejected the cookies"));
    }

    [TestMethod]
    public void ClassifyLine_AuthFamily()
    {
        Assert.AreEqual(YtdlpOutputClass.LoginRequired,
            YtdlpOutputClassifier.ClassifyLine("ERROR: [youtube] abc: login required to watch this video"));
        Assert.AreEqual(YtdlpOutputClass.AgeRestricted,
            YtdlpOutputClassifier.ClassifyLine("ERROR: [youtube] abc: age-restricted video"));
        Assert.AreEqual(YtdlpOutputClass.Private,
            YtdlpOutputClassifier.ClassifyLine("ERROR: [youtube] abc: private video"));
        Assert.AreEqual(YtdlpOutputClass.MembersOnly,
            YtdlpOutputClassifier.ClassifyLine("ERROR: [youtube] abc: members-only content"));
    }

    [TestMethod]
    public void ClassifyLine_DeadContent()
    {
        Assert.AreEqual(YtdlpOutputClass.DeadContent,
            YtdlpOutputClassifier.ClassifyLine("ERROR: [youtube] abc: Video unavailable"));
        Assert.AreEqual(YtdlpOutputClass.DeadContent,
            YtdlpOutputClassifier.ClassifyLine("ERROR: [youtube] abc: This video does not exist"));
    }

    [TestMethod]
    public void ClassifyLine_Retryable()
    {
        Assert.AreEqual(YtdlpOutputClass.Retryable,
            YtdlpOutputClassifier.ClassifyLine("ERROR: unable to download video data: HTTP Error 429"));
        Assert.AreEqual(YtdlpOutputClass.Retryable,
            YtdlpOutputClassifier.ClassifyLine("ERROR: [youtube] abc: Connection timed out"));
        Assert.AreEqual(YtdlpOutputClass.Retryable,
            YtdlpOutputClassifier.ClassifyLine("ERROR: gvs.googlevideo.com returned HTTP Error 403"));
    }

    [TestMethod]
    public void ClassifyLine_Unknown()
    {
        Assert.AreEqual(YtdlpOutputClass.Unknown,
            YtdlpOutputClassifier.ClassifyLine("Some random output line"));
        Assert.AreEqual(YtdlpOutputClass.Unknown, YtdlpOutputClassifier.ClassifyLine(""));
    }

    [TestMethod]
    public void Classify_MostSignificantVerdictWins()
    {
        // A rotated-session WARNING outweighs a successful run: exit code 0
        // alone is never trusted.
        var output = new[]
        {
            "WARNING: [youtube] the cookies are no longer valid",
            "ERROR: something unrelated",
        };
        Assert.AreEqual(YtdlpOutputClass.SessionRotated,
            YtdlpOutputClassifier.Classify(output, runSucceeded: true));

        // Jar inconsistency is the most significant verdict of all.
        var mixed = new[]
        {
            "WARNING: cookies have been rotated",
            "ERROR: CookieMismatch",
        };
        Assert.AreEqual(YtdlpOutputClass.JarInconsistent,
            YtdlpOutputClassifier.Classify(mixed, runSucceeded: false));

        // Clean output keeps the run outcome.
        Assert.AreEqual(YtdlpOutputClass.Ok,
            YtdlpOutputClassifier.Classify(Array.Empty<string>(), runSucceeded: true));
        Assert.AreEqual(YtdlpOutputClass.Unknown,
            YtdlpOutputClassifier.Classify(new[] { "nothing matches" }, runSucceeded: false));
    }

    [TestMethod]
    public void IsAuthFamily_And_ToAuthFailureReason_Mapping()
    {
        Assert.IsTrue(YtdlpOutputClassifier.IsAuthFamily(YtdlpOutputClass.BotCheck));
        Assert.IsTrue(YtdlpOutputClassifier.IsAuthFamily(YtdlpOutputClass.LoginRequired));
        Assert.IsTrue(YtdlpOutputClassifier.IsAuthFamily(YtdlpOutputClass.AgeRestricted));
        Assert.IsFalse(YtdlpOutputClassifier.IsAuthFamily(YtdlpOutputClass.DeadContent));
        Assert.IsFalse(YtdlpOutputClassifier.IsAuthFamily(YtdlpOutputClass.SessionRotated));
        Assert.IsFalse(YtdlpOutputClassifier.IsAuthFamily(YtdlpOutputClass.Retryable));

        Assert.AreEqual(AuthFailureReason.AgeRestricted,
            YtdlpOutputClassifier.ToAuthFailureReason(YtdlpOutputClass.AgeRestricted));
        Assert.AreEqual(AuthFailureReason.MembersOnly,
            YtdlpOutputClassifier.ToAuthFailureReason(YtdlpOutputClass.MembersOnly));
    }

    [TestMethod]
    public void BuildExtractorArgs_Postures()
    {
        Assert.AreEqual("youtube:player_client=mweb",
            YouTubePosture.BuildExtractorArgs(YouTubePosture.CookieClients));
        Assert.AreEqual("youtube:player_client=mweb;visitor_data=abc",
            YouTubePosture.BuildExtractorArgs(YouTubePosture.CookieClients, "abc"));
        Assert.AreEqual("youtube:player_client=android_vr,mweb",
            YouTubePosture.BuildExtractorArgs(YouTubePosture.AnonymousClients));
    }
}
