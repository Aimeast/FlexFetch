using FlexFetch.Config;
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
}
