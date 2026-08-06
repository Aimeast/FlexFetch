using System.Text.RegularExpressions;
using FlexFetch.Domain;

namespace FlexFetch.Tests;

[TestClass]
public sealed class RandomIdTests
{
    private static readonly Regex UrlSafeBase64 = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    [TestMethod]
    public void New_ReturnsEightUrlSafeCharacters()
    {
        var id = RandomId.New();

        Assert.AreEqual(8, id.Length);
        StringAssert.Matches(id, UrlSafeBase64);
    }

    [TestMethod]
    public void New_IsUniqueAcrossManyGenerations()
    {
        const int count = 100_000;
        var ids = Enumerable.Range(0, count).Select(_ => RandomId.New()).ToArray();

        Assert.AreEqual(count, ids.Distinct().Count());
    }

    [TestMethod]
    public void New_ContainsNoPaddingOrSequentialPattern()
    {
        var a = RandomId.New();
        var b = RandomId.New();

        // Two consecutive IDs are not trivially related.
        Assert.AreNotEqual(a, b);
        Assert.DoesNotContain(a, "=");
        Assert.DoesNotContain(b, "=");
    }
}
