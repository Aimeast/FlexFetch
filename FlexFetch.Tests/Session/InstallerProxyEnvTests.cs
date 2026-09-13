using FlexFetch.Config;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Session;

namespace FlexFetch.Tests;

[TestClass]
public sealed class InstallerProxyEnvTests
{
    [TestMethod]
    public void EnvFor_HttpAndHttpsProxy_PassesThrough()
    {
        var http = InstallerProxyEnv.EnvFor("http://[::1]:1088");
        Assert.HasCount(2, http);
        Assert.AreEqual("http://[::1]:1088", http["HTTP_PROXY"]);
        Assert.AreEqual("http://[::1]:1088", http["HTTPS_PROXY"]);

        var https = InstallerProxyEnv.EnvFor("https://proxy.corp:3128");
        Assert.AreEqual("https://proxy.corp:3128", https["HTTPS_PROXY"]);
    }

    [TestMethod]
    public void EnvFor_SocksProxy_YieldsNoEnv()
    {
        // Installers (deno, the Playwright downloader) cannot use socks
        // proxies: a socks value in their environment hangs or fails the
        // download silently, so it must be omitted entirely.
        Assert.IsEmpty(InstallerProxyEnv.EnvFor("socks5://[::1]:1082"));
        Assert.IsEmpty(InstallerProxyEnv.EnvFor("socks5h://[::1]:1082"));
    }

    [TestMethod]
    public void EnvFor_NullOrEmpty_YieldsNoEnv()
    {
        Assert.IsEmpty(InstallerProxyEnv.EnvFor(null));
        Assert.IsEmpty(InstallerProxyEnv.EnvFor(" "));
    }

    [TestMethod]
    public void Resolve_DedicatedHttpProxy_WinsOverPrimarySocks()
    {
        var config = new TestConfig();
        config.Set(ConfigKeys.NetworkHttpProxy, "http://[::1]:1088");

        var env = InstallerProxyEnv.Resolve(
            config, new SocksProxyStub("socks5://[::1]:1082"), "https://cdn.playwright.dev/");

        Assert.HasCount(2, env);
        Assert.AreEqual("http://[::1]:1088", env["HTTP_PROXY"]);
    }

    [TestMethod]
    public void Resolve_NoDedicatedProxy_FallsBackToPrimaryWhenHttp()
    {
        var config = new TestConfig();
        var proxy = new SocksProxyStub("http://[::1]:1088");

        var env = InstallerProxyEnv.Resolve(config, proxy, "https://cdn.playwright.dev/");

        Assert.AreEqual("http://[::1]:1088", env["HTTP_PROXY"]);
    }

    [TestMethod]
    public void Resolve_SocksPrimaryAndNoDedicated_YieldsNoEnv()
    {
        var config = new TestConfig();

        var env = InstallerProxyEnv.Resolve(
            config, new SocksProxyStub("socks5://[::1]:1082"), "https://cdn.playwright.dev/");

        Assert.IsEmpty(env);
    }

    private sealed class SocksProxyStub : IProxyService
    {
        private readonly string? _proxyUri;

        public SocksProxyStub(string? proxyUri) => _proxyUri = proxyUri;

        public bool ShouldProxy(Uri url) => _proxyUri is not null;

        public bool ShouldProxyFast(Uri url) => _proxyUri is not null;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => _proxyUri;

        public string? GetBrowserProxyAddress() => _proxyUri;
    }
}
