using FlexFetch.Services.Session;

namespace FlexFetch.Tests;

[TestClass]
public sealed class FirefoxBrowserServiceTests
{
    private static string ExeName => OperatingSystem.IsWindows() ? "firefox.exe" : "firefox";

    [TestMethod]
    public void FindFirefoxExecutable_CustomBrowsersPath_LooksThere_AndBlankIgnored()
    {
        // Environment variables are process-global and this assembly runs
        // tests with method-level parallelism, so every mutation stays
        // inside this single test method.
        var root = CreateFakeBrowserInstall();
        try
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
}
