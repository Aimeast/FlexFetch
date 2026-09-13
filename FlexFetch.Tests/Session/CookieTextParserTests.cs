using FlexFetch.Services.Session;

namespace FlexFetch.Tests;

[TestClass]
public sealed class CookieTextParserTests
{
    [TestMethod]
    public void Parse_Netscape_ParsesHttpOnlyLines()
    {
        var text = string.Join("\n",
            "# Netscape HTTP Cookie File",
            "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1790000000\tLOGIN_INFO\tAFmmF2sw",
            ".youtube.com\tTRUE\t/\tFALSE\t1790000000\tPREF\tpref");

        var result = CookieTextParser.Parse(text);

        Assert.IsEmpty(result.Errors);
        Assert.HasCount(2, result.Cookies);
        Assert.IsTrue(result.Cookies[0].HttpOnly);
        Assert.AreEqual("LOGIN_INFO", result.Cookies[0].Name);
        Assert.IsFalse(result.Cookies[1].HttpOnly);
    }

    [TestMethod]
    public void Parse_BrowserJsonExport_ParsesEntries()
    {
        const string json = """
            {
              "cookies": [
                {
                  "domain": ".youtube.com",
                  "name": "SID",
                  "value": "sid-value",
                  "path": "/",
                  "secure": true,
                  "httpOnly": true,
                  "expirationDate": 1790000000
                },
                {
                  "domain": ".youtube.com",
                  "name": "YSC",
                  "value": "ysc",
                  "path": "/",
                  "session": true
                }
              ]
            }
            """;

        var result = CookieTextParser.Parse(json);

        Assert.IsEmpty(result.Errors);
        Assert.HasCount(2, result.Cookies);
        Assert.AreEqual("SID", result.Cookies[0].Name);
        Assert.IsNotNull(result.Cookies[0].ExpiresAt);
        // Session cookies carry no expiry.
        Assert.IsNull(result.Cookies[1].ExpiresAt);
    }

    [TestMethod]
    public void Parse_SetCookie_UsesUrlForDefaults()
    {
        var url = new Uri("https://www.youtube.com/watch?v=abc");
        const string header = "VISITOR_INFO1_LIVE=abc123; Path=/; Domain=.youtube.com; Secure; HttpOnly; Max-Age=3600";

        var result = CookieTextParser.Parse(header, url);

        Assert.IsEmpty(result.Errors);
        Assert.HasCount(1, result.Cookies);
        Assert.AreEqual("VISITOR_INFO1_LIVE", result.Cookies[0].Name);
        Assert.AreEqual("abc123", result.Cookies[0].Value);
        // The Domain attribute is stored without its leading dot.
        Assert.AreEqual("youtube.com", result.Cookies[0].Domain);
        Assert.IsTrue(result.Cookies[0].Secure);
        Assert.IsTrue(result.Cookies[0].HttpOnly);
        Assert.IsNotNull(result.Cookies[0].ExpiresAt);
    }

    [TestMethod]
    public void Parse_SetCookie_WithoutUrl_ReportsError()
    {
        var result = CookieTextParser.Parse("name=value; Path=/");

        Assert.HasCount(0, result.Cookies);
        Assert.HasCount(1, result.Errors);
    }

    [TestMethod]
    public void Parse_Netscape_MalformedLines_AreReportedNotDroppedSilently()
    {
        var text = string.Join("\n",
            "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1790000000\tSID\tsid",
            "not-a-cookie-line");

        var result = CookieTextParser.Parse(text);

        Assert.HasCount(1, result.Cookies);
        Assert.HasCount(1, result.Errors);
    }
}
