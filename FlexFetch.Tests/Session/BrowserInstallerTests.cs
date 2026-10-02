using FlexFetch.Services.Session;

namespace FlexFetch.Tests;

[TestClass]
public sealed class BrowserInstallerTests
{
    [TestMethod]
    public void Steps_Windows_InstallOnly()
    {
        // No apt on Windows: the OS-dependency step is Linux-only.
        CollectionAssert.AreEqual(
            new[] { "install", "firefox" },
            BrowserInstaller.Steps("firefox", isWindows: true)[0]);
        Assert.HasCount(1, BrowserInstaller.Steps("firefox", isWindows: true));
    }

    [TestMethod]
    public void Steps_Linux_DepsFirstThenInstall()
    {
        var steps = BrowserInstaller.Steps("firefox", isWindows: false);

        Assert.HasCount(2, steps);
        CollectionAssert.AreEqual(new[] { "install-deps", "firefox" }, steps[0]);
        CollectionAssert.AreEqual(new[] { "install", "firefox" }, steps[1]);
    }

    [TestMethod]
    public void MirrorProxyEnvToLowerCase_CopiesUppercase_AndSkipsBlank()
    {
        if (!OperatingSystem.IsLinux())
        {
            // Windows environment variable names are case-insensitive, so
            // the lowercase mirror targets the same variable as the
            // uppercase source and the copy cannot be observed there. The
            // mirrored variables are only consumed by apt on Linux.
            Assert.Inconclusive("Linux-only behavior");
            return;
        }

        // Environment variables are process-global and this assembly runs
        // tests with method-level parallelism, so every mutation stays
        // inside this single test method.
        WithEnv("HTTP_PROXY", "http://proxy.example.com:3128", () =>
        {
            WithEnv("HTTPS_PROXY", "http://proxy.example.com:3128", () =>
            {
                WithEnv("http_proxy", null, () =>
                {
                    WithEnv("https_proxy", null, () =>
                    {
                        BrowserInstaller.MirrorProxyEnvToLowerCase();

                        Assert.AreEqual("http://proxy.example.com:3128", Environment.GetEnvironmentVariable("http_proxy"));
                        Assert.AreEqual("http://proxy.example.com:3128", Environment.GetEnvironmentVariable("https_proxy"));
                    });
                });
            });
        });
    }

    [TestMethod]
    public void WriteAptProxyConf_LinuxWithProxy_WritesConfFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "aptconf-" + Guid.NewGuid().ToString("N") + Path.DirectorySeparatorChar + "95proxy");

        try
        {
            BrowserInstaller.WriteAptProxyConf("http://proxy.example.com:3128", isWindows: false, confPath: path);

            var content = File.ReadAllText(path);
            Assert.Contains("Acquire::http::Proxy \"http://proxy.example.com:3128\";", content);
            Assert.Contains("Acquire::https::Proxy \"http://proxy.example.com:3128\";", content);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    public void WriteAptProxyConf_WindowsOrBlank_NoFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "aptconf-" + Guid.NewGuid().ToString("N"));

        BrowserInstaller.WriteAptProxyConf("http://proxy.example.com:3128", isWindows: true, confPath: path);
        BrowserInstaller.WriteAptProxyConf(" ", isWindows: false, confPath: path);

        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void OsDepsInstalled_Windows_AlwaysTrue()
    {
        Assert.IsTrue(BrowserInstaller.OsDepsInstalled(
            Path.Combine(Path.GetTempPath(), "absent-marker"), "1.0.0", isWindows: true));
    }

    [TestMethod]
    public void OsDepsInstalled_Linux_MarkerVersion_MustMatchCurrent()
    {
        var marker = Path.Combine(Path.GetTempPath(), "osdeps-" + Guid.NewGuid().ToString("N") + ".marker");
        try
        {
            Assert.IsFalse(BrowserInstaller.OsDepsInstalled(marker, "1.0.0", isWindows: false));

            File.WriteAllText(marker, "1.0.0");
            Assert.IsTrue(BrowserInstaller.OsDepsInstalled(marker, "1.0.0", isWindows: false));

            File.WriteAllText(marker, "2.0.0");
            Assert.IsFalse(BrowserInstaller.OsDepsInstalled(marker, "1.0.0", isWindows: false));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    // --- PrepareAptSources ---

    private const string Deb822Sources = """
        Types: deb
        URIs: http://archive.ubuntu.com/ubuntu/
        Suites: noble noble-updates noble-backports
        Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg

        Types: deb
        URIs: http://security.ubuntu.com/ubuntu/
        Suites: noble-security
        Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
        """;

    private string WriteSourcesFile(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aptsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ubuntu.sources"), content);
        return dir;
    }

    [TestMethod]
    public void PrepareAptSources_NoMirror_UpgradesOfficialHostsToHttps()
    {
        var dir = WriteSourcesFile(Deb822Sources);

        var notices = BrowserInstaller.PrepareAptSources(null, isWindows: false, sourcesDir: dir, legacyList: Path.Combine(dir, "absent.list"));

        Assert.HasCount(1, notices);
        var content = File.ReadAllText(Path.Combine(dir, "ubuntu.sources"));
        Assert.Contains("https://archive.ubuntu.com/ubuntu/", content);
        Assert.Contains("https://security.ubuntu.com/ubuntu/", content);
        Assert.DoesNotContain("http://archive", content);
    }

    [TestMethod]
    public void PrepareAptSources_Mirror_PointsBothHostsAtMirrorOverHttps()
    {
        var dir = WriteSourcesFile(Deb822Sources);

        // Scheme and trailing slash in the mirror value are tolerated.
        var notices = BrowserInstaller.PrepareAptSources(
            "https://mirrors.example.com/ubuntu/", isWindows: false, sourcesDir: dir, legacyList: Path.Combine(dir, "absent.list"));

        Assert.HasCount(1, notices);
        Assert.Contains("mirrors.example.com/ubuntu", notices[0]);
        var content = File.ReadAllText(Path.Combine(dir, "ubuntu.sources"));
        Assert.Contains("https://mirrors.example.com/ubuntu/", content);
        Assert.DoesNotContain("ubuntu.com", content);
    }

    [TestMethod]
    public void PrepareAptSources_AlreadyHttps_NoRewrite()
    {
        var dir = WriteSourcesFile(Deb822Sources.Replace("http://", "https://", StringComparison.Ordinal));

        var notices = BrowserInstaller.PrepareAptSources(null, isWindows: false, sourcesDir: dir, legacyList: Path.Combine(dir, "absent.list"));

        Assert.IsEmpty(notices);
    }

    [TestMethod]
    public void PrepareAptSources_LegacySourcesList_Rewritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aptsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var legacy = Path.Combine(dir, "sources.list");
        File.WriteAllText(legacy, "deb http://archive.ubuntu.com/ubuntu noble main\n");

        var notices = BrowserInstaller.PrepareAptSources("mirrors.example.com/ubuntu", isWindows: false, sourcesDir: Path.Combine(dir, "absent"), legacyList: legacy);

        Assert.HasCount(1, notices);
        Assert.Contains("https://mirrors.example.com/ubuntu noble main", File.ReadAllText(legacy));
        Directory.Delete(dir, recursive: true);
    }

    [TestMethod]
    public void PrepareAptSources_Windows_NoOp()
    {
        var dir = WriteSourcesFile(Deb822Sources);

        Assert.IsEmpty(BrowserInstaller.PrepareAptSources("mirrors.example.com/ubuntu", isWindows: true, sourcesDir: dir));

        Assert.Contains("http://archive.ubuntu.com/ubuntu/", File.ReadAllText(Path.Combine(dir, "ubuntu.sources")));
        Directory.Delete(dir, recursive: true);
    }

    private static void WithEnv(string name, string? value, Action action)
    {
        var original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }
}
