using System.Net;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Services.Routing;
using Microsoft.Extensions.Configuration;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class ProxyServiceTests
{
    private static readonly ILogger Log = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();

    private static readonly string DataDir = Path.GetTempPath();

    [TestMethod]
    public void NoProxyConfigured_ConnectsDirect()
    {
        var config = new TestConfig();
        var service = new ProxyService(config, Log, DataDir);

        Assert.IsFalse(service.ShouldProxy(new Uri("https://example.com/file")));
        Assert.IsNull(service.GetProxyUri(new Uri("https://example.com/file")));
    }

    [TestMethod]
    public void ProxyConfigured_NoRules_UsesDefaultAction()
    {
        var config = new TestConfig();
        config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
        var service = new ProxyService(config, Log, DataDir);

        // No rules -> default action is UseProxy.
        Assert.IsTrue(service.ShouldProxy(new Uri("https://example.com/file")));
        Assert.AreEqual("socks5://127.0.0.1:1080", service.GetProxyUri(new Uri("https://example.com/file")));
    }

    [TestMethod]
    public void ProxyConfigured_DefaultDirect_ConnectsDirectly()
    {
        var config = new TestConfig();
        config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
        config.Set(ConfigKeys.DefaultAction, "Direct");
        var service = new ProxyService(config, Log, DataDir);

        // No rules, default Direct -> direct even though proxy is set.
        Assert.IsFalse(service.ShouldProxy(new Uri("https://example.com/file")));
        Assert.IsNull(service.GetProxyUri(new Uri("https://example.com/file")));
    }

    [TestMethod]
    public void Rule_DirectByCidr_OverridesDefaultUseProxy()
    {
        var dir = TestApp.CreateTempDataDir();
        try
        {
            var bypassFile = Path.Combine(dir, "bypass.txt");
            File.WriteAllLines(bypassFile, new[] { "10.0.0.0/8", "# comment" });

            var rules = $"[{{\"Action\":\"Direct\",\"CidrFiles\":[\"{bypassFile.Replace("\\", "\\\\")}\"]}}]";
            var config = new TestConfig();
            config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
            config.Set(ConfigKeys.RouteRules, rules);
            var service = new ProxyService(config, Log, DataDir);

            // 10.x matches the CIDR rule -> direct.
            Assert.IsFalse(service.ShouldProxy(new Uri("https://10.1.2.3/file")));
            Assert.IsNull(service.GetProxyUri(new Uri("https://10.1.2.3/file")));

            // 8.8.8.8 does not match -> default UseProxy.
            Assert.IsTrue(service.ShouldProxy(new Uri("https://8.8.8.8/file")));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Rule_DirectByDomain_OverridesDefaultUseProxy()
    {
        var rules = "[{\"Action\":\"Direct\",\"Domains\":[\"cn\"]}]";
        var config = new TestConfig();
        config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
        config.Set(ConfigKeys.RouteRules, rules);
        var service = new ProxyService(config, Log, DataDir);

        // .cn domains match the domain rule -> direct.
        Assert.IsFalse(service.ShouldProxy(new Uri("https://example.cn/path")));
        Assert.IsFalse(service.ShouldProxy(new Uri("https://www.example.com.cn/path")));
        Assert.IsNull(service.GetProxyUri(new Uri("https://example.cn/path")));

        // Non-.cn domains -> default UseProxy.
        Assert.IsTrue(service.ShouldProxy(new Uri("https://example.com/path")));
    }

    [TestMethod]
    public void Rule_UseProxy_OverridesDefaultDirect()
    {
        var rules = "[{\"Action\":\"UseProxy\",\"Domains\":[\"example.com\"]}]";
        var config = new TestConfig();
        config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
        config.Set(ConfigKeys.DefaultAction, "Direct");
        config.Set(ConfigKeys.RouteRules, rules);
        var service = new ProxyService(config, Log, DataDir);

        // example.com matches the UseProxy rule -> proxy.
        Assert.IsTrue(service.ShouldProxy(new Uri("https://example.com/path")));
        // Other hosts -> default Direct.
        Assert.IsFalse(service.ShouldProxy(new Uri("https://other.org/path")));
    }

    [TestMethod]
    public void StructuredRules_BindFromAppSettings()
    {
        // appsettings binds the rule list structurally (JSON array), not as
        // a JSON string; InMemoryCollection expresses arrays with index keys.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Network:Proxy"] = "http://127.0.0.1:3128",
                ["Network:DefaultAction"] = "UseProxy",
                ["Network:RouteRules:0:Action"] = "Direct",
                ["Network:RouteRules:0:Domains:0"] = "cn",
            })
            .Build();

        var service = new ProxyService(config, Log, DataDir);

        Assert.AreEqual("http://127.0.0.1:3128", service.GetProxyUri(new Uri("https://example.com/file")));
        // .cn direct per structured RouteRules.
        Assert.IsNull(service.GetProxyUri(new Uri("https://example.cn/file")));
    }

    [TestMethod]
    public void Rule_MultipleCidrFiles()
    {
        var dir = TestApp.CreateTempDataDir();
        try
        {
            var v4File = Path.Combine(dir, "v4.txt");
            var v6File = Path.Combine(dir, "v6.txt");
            File.WriteAllLines(v4File, new[] { "10.0.0.0/8" });
            File.WriteAllLines(v6File, new[] { "2001:db8::/32" });

            var rules = $"[{{\"Action\":\"Direct\",\"CidrFiles\":[\"{v4File.Replace("\\", "\\\\")}\",\"{v6File.Replace("\\", "\\\\")}\"]}}]";
            var config = new TestConfig();
            config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
            config.Set(ConfigKeys.RouteRules, rules);
            var service = new ProxyService(config, Log, DataDir);

            Assert.IsFalse(service.ShouldProxy(new Uri("https://10.1.2.3/file")));
            Assert.IsFalse(service.ShouldProxy(new Uri("https://[2001:db8::1]/file")));
            Assert.IsTrue(service.ShouldProxy(new Uri("https://8.8.8.8/file")));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CreateHandler_AppliesProxyWhenNeeded()
    {
        var config = new TestConfig();
        config.Set(ConfigKeys.Proxy, "http://127.0.0.1:3128");
        var service = new ProxyService(config, Log, DataDir);

        var handler = (SocketsHttpHandler)service.CreateHandler(new Uri("https://example.com/file"));
        Assert.IsTrue(handler.UseProxy);
        Assert.IsNotNull(handler.Proxy);
    }

    [TestMethod]
    public void ShouldProxyFast_NoProxy_ReturnsFalse()
    {
        var config = new TestConfig();
        var service = new ProxyService(config, Log, DataDir);

        Assert.IsFalse(service.ShouldProxyFast(new Uri("https://example.com/file")));
    }

    [TestMethod]
    public void ShouldProxyFast_DomainRule_MatchesWithoutDns()
    {
        var rules = "[{\"Action\":\"Direct\",\"Domains\":[\"cn\"]}]";
        var config = new TestConfig();
        config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
        config.Set(ConfigKeys.RouteRules, rules);
        var service = new ProxyService(config, Log, DataDir);

        // .cn domains match the Direct rule -> fast decision says no proxy.
        Assert.IsFalse(service.ShouldProxyFast(new Uri("https://example.cn/path")));
        Assert.IsFalse(service.ShouldProxyFast(new Uri("https://www.example.com.cn/path")));

        // Non-.cn hosts fall through to the default UseProxy action.
        Assert.IsTrue(service.ShouldProxyFast(new Uri("https://example.com/path")));
    }

    [TestMethod]
    public void ShouldProxyFast_DefaultDirect_ReturnsFalse()
    {
        var config = new TestConfig();
        config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
        config.Set(ConfigKeys.DefaultAction, "Direct");
        var service = new ProxyService(config, Log, DataDir);

        // No rules, default Direct -> fast decision says direct.
        Assert.IsFalse(service.ShouldProxyFast(new Uri("https://example.com/file")));
    }

    [TestMethod]
    public void ShouldProxyFast_CidrRule_SkippedWithoutDns()
    {
        var dir = TestApp.CreateTempDataDir();
        try
        {
            var bypassFile = Path.Combine(dir, "bypass.txt");
            File.WriteAllLines(bypassFile, new[] { "10.0.0.0/8" });

            var rules = $"[{{\"Action\":\"Direct\",\"CidrFiles\":[\"{bypassFile.Replace("\\", "\\\\")}\"]}}]";
            var config = new TestConfig();
            config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
            config.Set(ConfigKeys.RouteRules, rules);
            var service = new ProxyService(config, Log, DataDir);

            // The fast decision must not resolve IPs: the CIDR rule is not
            // evaluated, so the default action (UseProxy) applies.
            Assert.IsTrue(service.ShouldProxyFast(new Uri("https://example.com/file")));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void EvaluateDomainsOnly_IgnoresCidrOnlyRules()
    {
        var cidr = new CidrMatcher();
        cidr.Load(new[] { "10.0.0.0/8" });
        var chain = new RouteRuleChain(
            new IRouteRule[] { new RouteRule(RouteAction.Direct, null, cidr) },
            RouteAction.UseProxy);

        // Without resolved IPs the CIDR rule cannot match: default applies.
        Assert.AreEqual(RouteAction.UseProxy, chain.EvaluateDomainsOnly(new Uri("https://example.com/")));
    }

    [TestMethod]
    public void EvaluateDomainsOnly_MatchesDomainRules()
    {
        var chain = new RouteRuleChain(
            new IRouteRule[] { new RouteRule(RouteAction.Direct, new DomainSuffixMatcher(new[] { "cn" }), null) },
            RouteAction.UseProxy);

        Assert.AreEqual(RouteAction.Direct, chain.EvaluateDomainsOnly(new Uri("https://example.cn/")));
        Assert.AreEqual(RouteAction.UseProxy, chain.EvaluateDomainsOnly(new Uri("https://example.com/")));
    }
}
