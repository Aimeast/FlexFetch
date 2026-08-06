using System.Net;
using FlexFetch.Services.Routing;

namespace FlexFetch.Tests;

[TestClass]
public sealed class CidrMatcherTests
{
    [TestMethod]
    public void MatchesIpv4WithinRange()
    {
        var matcher = new CidrMatcher();
        matcher.Load(new[] { "10.0.0.0/8" });

        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("10.1.2.3")));
        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("10.255.255.255")));
        Assert.IsFalse(matcher.IsMatch(IPAddress.Parse("11.0.0.1")));
        Assert.IsFalse(matcher.IsMatch(IPAddress.Parse("192.168.1.1")));
    }

    [TestMethod]
    public void MatchesIpv4SingleAddress()
    {
        var matcher = new CidrMatcher();
        matcher.Load(new[] { "192.168.1.100" });

        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("192.168.1.100")));
        Assert.IsFalse(matcher.IsMatch(IPAddress.Parse("192.168.1.101")));
    }

    [TestMethod]
    public void MatchesIpv4SubnetBoundaries()
    {
        var matcher = new CidrMatcher();
        matcher.Load(new[] { "192.168.1.0/24" });

        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("192.168.1.0")));
        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("192.168.1.255")));
        Assert.IsFalse(matcher.IsMatch(IPAddress.Parse("192.168.2.0")));
        Assert.IsFalse(matcher.IsMatch(IPAddress.Parse("192.168.0.255")));
    }

    [TestMethod]
    public void MatchesIpv6WithinRange()
    {
        var matcher = new CidrMatcher();
        matcher.Load(new[] { "2001:db8::/32" });

        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("2001:db8::1")));
        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("2001:db8:ffff::1")));
        Assert.IsFalse(matcher.IsMatch(IPAddress.Parse("2001:db9::1")));
        Assert.IsFalse(matcher.IsMatch(IPAddress.Parse("::1")));
    }

    [TestMethod]
    public void MatchesIpv4MappedIpv6AsIpv4()
    {
        var matcher = new CidrMatcher();
        matcher.Load(new[] { "10.0.0.0/8" });

        // ::ffff:10.1.2.3 is the IPv4-mapped form of 10.1.2.3.
        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("::ffff:10.1.2.3")));
    }

    [TestMethod]
    public void MergesAdjacentRanges()
    {
        var matcher = new CidrMatcher();
        matcher.Load(new[] { "10.0.0.0/9", "10.128.0.0/9" });

        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("10.1.1.1")));
        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("10.200.200.200")));
        Assert.IsFalse(matcher.IsMatch(IPAddress.Parse("11.0.0.1")));
    }

    [TestMethod]
    public void HandlesZeroSubnet()
    {
        var matcher = new CidrMatcher();
        matcher.Load(new[] { "0.0.0.0/0" });

        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("8.8.8.8")));
        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("192.168.0.1")));
    }

    [TestMethod]
    public void SkipsCommentsAndInvalidLines()
    {
        var matcher = new CidrMatcher();
        var rejected = new List<string>();
        matcher.Load(
            new[] { "# comment", "   ", "10.0.0.0/8", "not-a-cidr", "999.999.999.999/8" },
            rejected.Add);

        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("10.1.1.1")));
        Assert.IsFalse(matcher.IsMatch(IPAddress.Parse("11.0.0.1")));
        Assert.IsGreaterThan(0, rejected.Count);
    }

    [TestMethod]
    public void EmptyLoad_NeverMatches()
    {
        var matcher = new CidrMatcher();
        matcher.Load(Array.Empty<string>());

        Assert.IsFalse(matcher.IsMatch(IPAddress.Parse("1.2.3.4")));
        Assert.IsFalse(matcher.IsMatch(IPAddress.Parse("2001:db8::1")));
    }

    [TestMethod]
    public void ReplaceFrom_SwapsRules()
    {
        var matcher = new CidrMatcher();
        matcher.Load(new[] { "10.0.0.0/8" });

        var other = new CidrMatcher();
        other.Load(new[] { "172.16.0.0/12" });
        matcher.ReplaceFrom(other);

        Assert.IsFalse(matcher.IsMatch(IPAddress.Parse("10.1.1.1")));
        Assert.IsTrue(matcher.IsMatch(IPAddress.Parse("172.16.5.5")));
    }
}
