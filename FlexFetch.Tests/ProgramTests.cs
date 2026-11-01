using FlexFetch.Config;
using FlexFetch.Startup;
using Microsoft.Extensions.Configuration;

namespace FlexFetch.Tests;

[TestClass]
public sealed class ProgramTests
{
    [TestMethod]
    public void ResolveDataDir_ExplicitConfig_Wins()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ConfigKeys.DataDir.Replace('.', ':')] = "/custom-data",
        }).Build();

        Assert.AreEqual("/custom-data", Program.ResolveDataDir(config));
    }

    [TestMethod]
    public void ResolveDataDir_Unconfigured_DefaultFolderOutsideDataMount()
    {
        var config = new ConfigurationBuilder().Build();

        // The /data adoption only triggers on Linux where the mount exists;
        // everywhere else the default hidden folder applies.
        var expected = OperatingSystem.IsLinux() && Directory.Exists("/data")
            ? "/data"
            : ConfigRegistry.GetDefault(ConfigKeys.DataDir);
        Assert.AreEqual(expected, Program.ResolveDataDir(config));
    }

    [TestMethod]
    public void ResolveDenoDir_Unpinned_ComputesDataCacheFolder()
    {
        // The deno compile cache is pinned next to the components, inside
        // the persistent data root - never a home directory deployments may
        // not even define.
        var dataDir = TestApp.CreateTempDataDir();
        Assert.AreEqual(
            Path.Combine(dataDir, "cache", "deno"),
            RuntimeSetup.ResolveDenoDir(dataDir, null));
        Assert.AreEqual(
            Path.Combine(dataDir, "cache", "deno"),
            RuntimeSetup.ResolveDenoDir(dataDir, "  "));
    }

    [TestMethod]
    public void ResolveDenoDir_PinnedByDeployment_KeepsTheExternalValue()
    {
        // Overriding an external DENO_DIR would split the boot warm-up's
        // cache from the cache the plugin's probes hit.
        Assert.IsNull(RuntimeSetup.ResolveDenoDir("/data", "/opt/deno-cache"));
    }
}
