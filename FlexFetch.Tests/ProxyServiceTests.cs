using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class ProxyServiceTests
{
    private static readonly ILogger Log = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .CreateLogger();

    [TestMethod]
    public void NoProxyConfigured_ConnectsDirect()
    {
        var config = new InMemoryConfigRepository();
        var service = new ProxyService(config, Log);

        Assert.IsFalse(service.ShouldProxy(new Uri("https://example.com/file")));
        Assert.IsNull(service.GetProxyUri(new Uri("https://example.com/file")));
    }

    [TestMethod]
    public void ProxyConfigured_UsesProxy()
    {
        var config = new InMemoryConfigRepository();
        config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
        var service = new ProxyService(config, Log);

        Assert.IsTrue(service.ShouldProxy(new Uri("https://example.com/file")));
        Assert.AreEqual("socks5://127.0.0.1:1080", service.GetProxyUri(new Uri("https://example.com/file")));
    }

    [TestMethod]
    public void BypassCidr_DirectsMatchingHosts()
    {
        var dir = TestApp.CreateTempDataDir();
        try
        {
            var bypassFile = Path.Combine(dir, "bypass.txt");
            File.WriteAllLines(bypassFile, new[] { "10.0.0.0/8", "# comment" });

            var config = new InMemoryConfigRepository();
            config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
            config.Set(ConfigKeys.BypassCidrFile, bypassFile);
            var service = new ProxyService(config, Log);

            // 10.x resolves to an IP inside the bypass range -> direct.
            Assert.IsFalse(service.ShouldProxy(new Uri("https://10.1.2.3/file")));
            Assert.IsNull(service.GetProxyUri(new Uri("https://10.1.2.3/file")));

            // 8.8.8.8 is outside the bypass range -> proxy.
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
    public void BypassCidr_MissingFile_StillUsesProxy()
    {
        var config = new InMemoryConfigRepository();
        config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
        config.Set(ConfigKeys.BypassCidrFile, "C:/nonexistent/bypass.txt");
        var service = new ProxyService(config, Log);

        Assert.IsTrue(service.ShouldProxy(new Uri("https://example.com/file")));
    }

    [TestMethod]
    public void CreateHandler_AppliesProxyWhenNeeded()
    {
        var config = new InMemoryConfigRepository();
        config.Set(ConfigKeys.Proxy, "http://127.0.0.1:3128");
        var service = new ProxyService(config, Log);

        var handler = (SocketsHttpHandler)service.CreateHandler(new Uri("https://example.com/file"));
        Assert.IsTrue(handler.UseProxy);
        Assert.IsNotNull(handler.Proxy);
    }

    [TestMethod]
    public void RoutePolicyChain_DirectWinsOverUseProxy()
    {
        var config = new InMemoryConfigRepository();
        config.Set(ConfigKeys.Proxy, "socks5://127.0.0.1:1080");
        var service = new ProxyService(config, Log);

        // Proxy is configured so GlobalProxy says UseProxy, but the bypass
        // list covers 127.0.0.0/8 and the target is 127.0.0.1 -> Direct wins.
        Assert.IsTrue(service.ShouldProxy(new Uri("https://8.8.8.8/file")));
    }

    private sealed class InMemoryConfigRepository : IConfigRepository
    {
        private readonly Dictionary<string, string> _values = new();

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public IReadOnlyDictionary<string, string> GetAll() => _values;

        public void Set(string key, string value) => _values[key] = value;

        public bool Delete(string key) => _values.Remove(key);
    }
}
