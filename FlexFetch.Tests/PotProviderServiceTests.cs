using FlexFetch.Config;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Session;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class PotProviderServiceTests
{
    private readonly string _dataDir = TestApp.CreateTempDataDir();

    private PotProviderService CreateService()
    {
        ILogger log = TestLog.Instance;
        var proxy = new ProxyService(new TestConfig(), log, _dataDir);
        var ytdlp = new YtdlpService(proxy, log, _dataDir);
        return new PotProviderService(ytdlp, proxy, log);
    }

    private string ServerDir()
    {
        var server = Path.Combine(
            _dataDir, "components", "bgutil-ytdlp-pot-provider", "server");
        Directory.CreateDirectory(Path.Combine(server, "src"));
        return server;
    }

    private void WriteFile(string path, string content = "x")
    {
        File.WriteAllText(path, content);
    }

    [TestMethod]
    public void ScriptWithoutManifest_IsNotInstalled()
    {
        var server = ServerDir();
        WriteFile(Path.Combine(server, "src", "generate_once.ts"));

        Assert.IsFalse(CreateService().IsServerInstalled);
    }

    [TestMethod]
    public void ManifestWithoutScript_IsNotInstalled()
    {
        var server = ServerDir();
        WriteFile(Path.Combine(server, "package.json"));

        Assert.IsFalse(CreateService().IsServerInstalled);
    }

    [TestMethod]
    public void ScriptAndManifest_AreInstalled()
    {
        var server = ServerDir();
        WriteFile(Path.Combine(server, "src", "generate_once.ts"));
        WriteFile(Path.Combine(server, "package.json"));

        Assert.IsTrue(CreateService().IsServerInstalled);
    }

    [TestMethod]
    public void EmptyTree_IsNotInstalled()
    {
        ServerDir();

        Assert.IsFalse(CreateService().IsServerInstalled);
    }
}
