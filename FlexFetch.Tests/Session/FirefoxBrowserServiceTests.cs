using FlexFetch.Services;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Session;

namespace FlexFetch.Tests;

[TestClass]
public sealed class FirefoxBrowserServiceTests
{
    private static string ExeName => OperatingSystem.IsWindows() ? "firefox.exe" : "firefox";

    // Environment variables are process-global and this assembly runs tests
    // with method-level parallelism: the env-mutating tests serialize on
    // this lock so they cannot steal each other's browsers path.
    private static readonly object EnvLock = new();

    [TestMethod]
    public void OsDepsMarker_LivesOutsideTheBrowserRoot()
    {
        // Regression: a volume-persistent marker survived container recreation
        // while the apt-installed libraries did not, leaving Firefox unable
        // to load libgtk-3. The marker must track the ephemeral filesystem
        // the libraries are installed into, not the persistent browser root.
        var browser = new FirefoxBrowserService(
            new NoProxyStub(),
            new StorageService(TestApp.CreateTempDataDir()),
            TestLog.Instance);

        Assert.IsFalse(browser.OsDepsMarkerPath
            .StartsWith(FirefoxBrowserService.BrowserRoot(), StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildNameFromExecutable_ExtractsPlaywrightBuild()
    {
        // The success log must name WHAT was installed, like the other
        // component installers do ("yt-dlp 2026.08.19 installed").
        var exe = Path.Combine("root", "firefox-1532", "firefox",
            OperatingSystem.IsWindows() ? "firefox.exe" : "firefox");
        Assert.AreEqual("firefox-1532", FirefoxBrowserService.BuildNameFromExecutable(exe));
        Assert.AreEqual("unknown build", FirefoxBrowserService.BuildNameFromExecutable(null));
        Assert.AreEqual("unknown build", FirefoxBrowserService.BuildNameFromExecutable(
            Path.Combine("root", "firefox.exe")));
    }

    [TestMethod]
    public void FindFirefoxExecutable_CustomBrowsersPath_LooksThere_AndBlankIgnored()
    {
        // Environment variables are process-global and this assembly runs
        // tests with method-level parallelism, so every mutation stays
        // inside this single test method (and under EnvLock).
        var root = CreateFakeBrowserInstall();
        try
        {
            lock (EnvLock)
            {
                WithBrowsersPath(root, () =>
                {
                    var found = FirefoxBrowserService.FindFirefoxExecutable();

                    Assert.IsNotNull(found);
                    Assert.IsTrue(found.StartsWith(root, StringComparison.Ordinal));
                });

                WithBrowsersPath(" ", () =>
                {
                    var found = FirefoxBrowserService.FindFirefoxExecutable();

                    // A real browser cache may exist in the default roots; the
                    // guarantee is only that a blank override is not honored.
                    if (found is not null)
                    {
                        Assert.IsFalse(found.StartsWith(root, StringComparison.Ordinal));
                    }
                });
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void BuildMarker_MissingOrStale_NotCurrent()
    {
        var root = CreateFakeBrowserInstall();
        try
        {
            // No marker: a build placed by any other means (or by an older
            // Playwright package) must not count as ready - the driver
            // launches the exact build its package pins.
            Assert.IsFalse(FirefoxBrowserService.IsFirefoxBuildCurrent(root));

            File.WriteAllText(Path.Combine(root, FirefoxBrowserService.BuildMarkerName), "0.0.0-stale");
            Assert.IsFalse(FirefoxBrowserService.IsFirefoxBuildCurrent(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void BuildMarker_WriteRecordsRunningPackageVersion()
    {
        var root = CreateFakeBrowserInstall();
        try
        {
            FirefoxBrowserService.WriteFirefoxBuildMarker(root);

            Assert.IsTrue(FirefoxBrowserService.IsFirefoxBuildCurrent(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void IsFirefoxReady_ExecutablePlusCurrentMarker()
    {
        var root = CreateFakeBrowserInstall();
        var browser = new FirefoxBrowserService(
            new NoProxyStub(),
            new StorageService(TestApp.CreateTempDataDir()),
            TestLog.Instance);
        try
        {
            lock (EnvLock)
            {
                WithBrowsersPath(root, () =>
                {
                    FirefoxBrowserService.WriteFirefoxBuildMarker();

                    // Windows has no OS-dependency layer (no apt): the executable
                    // plus a current build marker suffice. Elsewhere the OS-deps
                    // marker is additionally required and stays missing here.
                    Assert.AreEqual(OperatingSystem.IsWindows(), browser.IsFirefoxReady());
                });
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateFakeBrowserInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "ffx-" + Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(root, "firefox-9999", "firefox", ExeName);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, string.Empty);
        return root;
    }

    private static void WithBrowsersPath(string? value, Action action)
    {
        var original = Environment.GetEnvironmentVariable(FirefoxBrowserService.BrowsersPathEnv);
        Environment.SetEnvironmentVariable(FirefoxBrowserService.BrowsersPathEnv, value);
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(FirefoxBrowserService.BrowsersPathEnv, original);
        }
    }

    private sealed class NoProxyStub : IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public bool ShouldProxyFast(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;
    }
}
