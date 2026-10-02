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
        var http = InstallerProxyEnv.EnvFor("http://proxy.example.com:3128");
        Assert.HasCount(2, http);
        Assert.AreEqual("http://proxy.example.com:3128", http["HTTP_PROXY"]);
        Assert.AreEqual("http://proxy.example.com:3128", http["HTTPS_PROXY"]);

        var https = InstallerProxyEnv.EnvFor("https://proxy.example.com:3128");
        Assert.AreEqual("https://proxy.example.com:3128", https["HTTPS_PROXY"]);
    }

    [TestMethod]
    public void EnvFor_SocksProxy_YieldsNoEnv()
    {
        // Installers (deno, the Playwright downloader) cannot use socks
        // proxies: a socks value in their environment hangs or fails the
        // download silently, so it must be omitted entirely.
        Assert.IsEmpty(InstallerProxyEnv.EnvFor("socks5://socks.example.net:1080"));
        Assert.IsEmpty(InstallerProxyEnv.EnvFor("socks5h://socks.example.net:1080"));
    }

    [TestMethod]
    public void EnvFor_NullOrEmpty_YieldsNoEnv()
    {
        Assert.IsEmpty(InstallerProxyEnv.EnvFor(null));
        Assert.IsEmpty(InstallerProxyEnv.EnvFor(" "));
    }

    [TestMethod]
    public void Resolve_HttpPrimary_PassedToInstaller()
    {
        var env = InstallerProxyEnv.Resolve(
            new SocksProxyStub("http://proxy.example.com:3128"), "https://cdn.playwright.dev/");

        Assert.HasCount(2, env);
        Assert.AreEqual("http://proxy.example.com:3128", env["HTTP_PROXY"]);
    }

    [TestMethod]
    public void Resolve_SocksPrimary_YieldsNoEnv()
    {
        // The single network.proxy address is socks: installers cannot use
        // it and run direct against mirrored registries.
        var env = InstallerProxyEnv.Resolve(
            new SocksProxyStub("socks5://socks.example.net:1080"), "https://cdn.playwright.dev/");

        Assert.IsEmpty(env);
    }

    [TestMethod]
    public void Resolve_NoProxy_YieldsNoEnv()
    {
        var env = InstallerProxyEnv.Resolve(
            new SocksProxyStub(null), "https://cdn.playwright.dev/");

        Assert.IsEmpty(env);
    }

    [TestMethod]
    public void Describe_ReportsProxyOrDirect()
    {
        Assert.AreEqual("http://proxy.example.com:3128",
            InstallerProxyEnv.Describe(InstallerProxyEnv.EnvFor("http://proxy.example.com:3128")));
        // Socks proxies yield no env: the install runs direct.
        Assert.AreEqual("direct", InstallerProxyEnv.Describe(InstallerProxyEnv.EnvFor("socks5://socks.example.net:1080")));
        Assert.AreEqual("direct", InstallerProxyEnv.Describe(new Dictionary<string, string>()));
    }

    private sealed class SocksProxyStub : IProxyService
    {
        private readonly string? _proxyUri;

        public SocksProxyStub(string? proxyUri) => _proxyUri = proxyUri;

        public bool ShouldProxy(Uri url) => _proxyUri is not null;

        public bool ShouldProxyFast(Uri url) => _proxyUri is not null;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => _proxyUri;

    }
}
