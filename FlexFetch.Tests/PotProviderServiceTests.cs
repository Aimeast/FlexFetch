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

    private PotProviderService CreateService(
        Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task>? warmUpRunner = null)
    {
        ILogger log = TestLog.Instance;
        var proxy = new ProxyService(new TestConfig(), log, _dataDir);
        var ytdlp = new YtdlpService(proxy, log, _dataDir);
        return new PotProviderService(ytdlp, proxy, log, warmUpRunner);
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Scaffolds an installed-enough component tree: the bgutil
    /// script + manifest and a deno binary placeholder (the warm-up never
    /// spawns a process under an injected runner, but its guard checks the
    /// files exist).</summary>
    private string ScaffoldInstalled()
    {
        var server = ServerDir();
        WriteFile(Path.Combine(server, "src", "generate_once.ts"));
        WriteFile(Path.Combine(server, "package.json"));
        WriteFile(Path.Combine(_dataDir, "components", OperatingSystem.IsWindows() ? "deno.exe" : "deno"));
        return server;
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

    [TestMethod]
    public async Task WarmUp_RunsPluginProbeCommand_OncePerProcess()
    {
        // The warm-up must mirror the plugin's script-deno probe (the
        // command whose 15s budget cold deno starts exceed), and run only
        // once per process on success.
        var server = ScaffoldInstalled();

        var runs = new List<System.Diagnostics.ProcessStartInfo>();
        var service = CreateService((psi, _) =>
        {
            runs.Add(psi);
            return Task.CompletedTask;
        });

        await service.WarmUpDenoAsync();
        await service.WarmUpDenoAsync();

        Assert.AreEqual(1, runs.Count, "a successful warm-up is memoized for the process lifetime");
        var psi = runs[0];
        StringAssert.StartsWith(Path.GetFileName(psi.FileName), "deno");
        CollectionAssert.AreEqual(
            new[]
            {
                "run",
                "--allow-env",
                "--allow-net",
                $"--allow-ffi={Path.Combine(_dataDir, "components", "bgutil-ytdlp-pot-provider", "server", "node_modules")}",
                psi.ArgumentList[4],
                psi.ArgumentList[5],
                Path.Combine(server, "src", "generate_once.ts"),
                "--version",
            },
            psi.ArgumentList.ToList());
        StringAssert.StartsWith(psi.ArgumentList[4], "--allow-write=");
        StringAssert.StartsWith(psi.ArgumentList[5], "--allow-read=");
        // The cache directory appears first in the read allow-list, then the
        // node_modules tree (same order the plugin passes).
        StringAssert.Contains(psi.ArgumentList[5], psi.ArgumentList[4]["--allow-write=".Length..]);
        Assert.AreEqual("1", psi.Environment["DENO_NO_PROMPT"]);
        Assert.AreEqual("1", psi.Environment["DENO_NO_UPDATE_CHECK"]);
        Assert.AreEqual("false", psi.Environment["FORCE_COLOR"]);
    }

    [TestMethod]
    public async Task WarmUp_Failure_IsRetriedOnNextCall()
    {
        ScaffoldInstalled();

        var calls = 0;
        var service = CreateService((_, _) =>
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.CompletedTask;
        });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.WarmUpDenoAsync());
        await service.WarmUpDenoAsync();

        Assert.AreEqual(2, calls, "a failed warm-up must not be memoized");
    }

    [TestMethod]
    public async Task WarmUp_SkipsWhenScriptMissing()
    {
        // No scaffold: the components are not installed, so the warm-up is
        // a no-op (the supervisor's install step runs before it anyway).
        var calls = 0;
        var service = CreateService((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await service.WarmUpDenoAsync();

        Assert.AreEqual(0, calls);
    }
}
