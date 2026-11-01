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
        Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task>? warmUpRunner = null,
        Func<DateTime>? utcClock = null)
    {
        ILogger log = TestLog.Instance;
        var proxy = new ProxyService(new TestConfig(), log, _dataDir);
        var ytdlp = new YtdlpService(proxy, log, _dataDir);
        return new PotProviderService(ytdlp, proxy, log, warmUpRunner, utcClock);
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
    public async Task WarmUp_RunsPluginProbeCommand_AndMemoizesWhileDenoUnchanged()
    {
        // The warm-up must mirror the plugin's script-deno probe (the
        // command whose 15s budget cold deno starts exceed), and is skipped
        // while the deno binary stays the same.
        var server = ScaffoldInstalled();

        var runs = new List<System.Diagnostics.ProcessStartInfo>();
        var service = CreateService((psi, _) =>
        {
            runs.Add(psi);
            return Task.CompletedTask;
        });

        await service.WarmUpDenoAsync();
        await service.WarmUpDenoAsync();

        Assert.AreEqual(1, runs.Count, "a successful warm-up is memoized on the deno binary's identity");
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
    public async Task WarmUp_ReRunsWhenDenoBinaryChanges()
    {
        // The auto-upgrade replaces the deno binary mid-process and deno's
        // compile cache is keyed by its version: without a re-warm the next
        // plugin probe hits a cold deno and exceeds its 15s budget.
        ScaffoldInstalled();
        var denoPath = Path.Combine(
            _dataDir, "components", OperatingSystem.IsWindows() ? "deno.exe" : "deno");

        var runs = new List<System.Diagnostics.ProcessStartInfo>();
        var service = CreateService((psi, _) =>
        {
            runs.Add(psi);
            return Task.CompletedTask;
        });

        await service.WarmUpDenoAsync();
        File.WriteAllText(denoPath, "upgraded");
        await service.WarmUpDenoAsync();

        Assert.AreEqual(2, runs.Count, "a replaced deno binary invalidates the warm-up memo");
    }

    [TestMethod]
    public async Task WarmUp_RerunsAfterTheRefreshInterval()
    {
        // Deno's compile cache can go cold without the binary changing; the
        // refresh interval bounds how long a silently lost cache starves the
        // PO token supply.
        ScaffoldInstalled();
        var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        var runs = 0;
        var service = CreateService((_, _) =>
        {
            runs++;
            return Task.CompletedTask;
        }, utcClock: () => now);

        await service.WarmUpDenoAsync();
        now += PotProviderService.WarmUpRefreshInterval - TimeSpan.FromMinutes(1);
        await service.WarmUpDenoAsync();
        Assert.AreEqual(1, runs, "a fresh warm-up is not re-run inside the refresh interval");

        now += TimeSpan.FromMinutes(2);
        await service.WarmUpDenoAsync();
        Assert.AreEqual(2, runs, "a stale warm-up memo re-runs the compile");
    }

    [TestMethod]
    public async Task InvalidateWarmUp_ForcesTheNextWarmUpToRun()
    {
        // A probe timeout inside yt-dlp is the one signal that the compile
        // cache went cold while the memo still claims it is hot.
        ScaffoldInstalled();
        var runs = 0;
        var service = CreateService((_, _) =>
        {
            runs++;
            return Task.CompletedTask;
        });

        await service.WarmUpDenoAsync();
        service.InvalidateWarmUp();
        await service.WarmUpDenoAsync();

        Assert.AreEqual(2, runs, "an invalidated memo re-runs the warm-up on the next call");
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

    [TestMethod]
    public void InstalledPluginWrapper_IsDetected()
    {
        // Regression: the install and the installed check used different
        // wrapper names (a hyphen apart), so every boot re-downloaded the
        // plugin while the extracted copy sat in the other directory.
        Directory.CreateDirectory(Path.Combine(
            _dataDir, "components", "yt-dlp-plugins", "bgutil-ytdlpotprovider"));

        Assert.IsTrue(CreateService().IsPluginInstalled);
    }

    [TestMethod]
    public void TimeoutPatch_RewritesTheStockBudgets()
    {
        // The plugin ships budgets tuned for fast machines: 20s per token
        // request and 15s for the script probe. On a slow host the script
        // start alone exceeds them and the run loses its token (HTTP 403 on
        // the media stream), so the installed copy is widened.
        const string baseSource = "class BgUtilPTPBase:\n    _GETPOT_TIMEOUT = 20.0\n";
        Assert.IsTrue(PotProviderService.TryReplaceTimeoutAssignment(
            baseSource, "_GETPOT_TIMEOUT", 20, PotProviderService.PluginTokenTimeoutSeconds, out var patchedBase));
        StringAssert.Contains(patchedBase, "_GETPOT_TIMEOUT = 120.0");
        Assert.IsFalse(patchedBase.Contains("_GETPOT_TIMEOUT = 20.0", StringComparison.Ordinal));

        const string scriptSource = "class BgUtilScriptPTPBase:\n    _GET_SCRIPT_VSN_TIMEOUT = 15.0\n";
        Assert.IsTrue(PotProviderService.TryReplaceTimeoutAssignment(
            scriptSource, "_GET_SCRIPT_VSN_TIMEOUT", 15, PotProviderService.PluginProbeTimeoutSeconds, out var patchedScript));
        StringAssert.Contains(patchedScript, "_GET_SCRIPT_VSN_TIMEOUT = 60.0");
    }

    [TestMethod]
    public void TimeoutPatch_IsIdempotentAndRejectsUnknownShapes()
    {
        // A second boot re-applies nothing; a plugin that no longer carries
        // the stock assignment must be reported instead of silently keeping
        // the old budget (the caller logs the warning).
        Assert.IsTrue(PotProviderService.TryReplaceTimeoutAssignment(
            "    _GETPOT_TIMEOUT = 120.0\n", "_GETPOT_TIMEOUT", 20, 120, out var alreadyPatched));
        Assert.AreEqual("    _GETPOT_TIMEOUT = 120.0\n", alreadyPatched);

        Assert.IsFalse(PotProviderService.TryReplaceTimeoutAssignment(
            "    _GETPOT_TIMEOUT = 30.0\n", "_GETPOT_TIMEOUT", 20, 120, out var untouched));
        Assert.AreEqual("    _GETPOT_TIMEOUT = 30.0\n", untouched);
    }
}
