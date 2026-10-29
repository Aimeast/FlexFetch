using FlexFetch.Services.Session;

namespace FlexFetch.Startup;

/// <summary>
/// Child-process entry used by the startup service to install the headless
/// browser: runs Playwright's OS-dependency and install commands and exits
/// without starting the web application. Invoked from Program with
/// <c>--install-browser [name]</c>; never returns.
/// </summary>
public static class BrowserInstallChild
{
    public static void Run(string[] args)
    {
        var browserName = args.Length > 1 ? args[1] : "firefox";
        // The parent process normally exports the browser cache location; a
        // manually invoked child resolves it from the data directory itself.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FirefoxBrowserService.BrowsersPathEnv)))
        {
            var childDataDir = RuntimeSetup.ResolveDataDir(
                new ConfigurationBuilder().AddEnvironmentVariables().Build());
            Environment.SetEnvironmentVariable(
                FirefoxBrowserService.BrowsersPathEnv,
                Path.Combine(Path.GetFullPath(childDataDir), "components", "ms-playwright"));
        }

        // A publish transferred from Windows lands without the driver node
        // binary's execute bit; repair it here so the driver can start at all.
        // Notices go to stderr: the parent keeps the stderr head in failure
        // reports and the whole stderr in the success log, so either way the
        // notice surfaces.
        foreach (var notice in BrowserInstaller.EnsureNodeExecutable(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PLAYWRIGHT_NODEJS_PATH")))
        {
            Console.Error.WriteLine(notice);
        }

        var exitCode = 0;
        foreach (var step in BrowserInstaller.Steps(browserName, OperatingSystem.IsWindows()))
        {
            if (step[0] == "install-deps")
            {
                // apt only reads the lowercase proxy variables, and its fetches
                // are the least reliable when driven by environment variables
                // alone - mirror them and pin the proxy in apt's own config.
                BrowserInstaller.MirrorProxyEnvToLowerCase();
                BrowserInstaller.WriteAptProxyConf(
                    Environment.GetEnvironmentVariable("HTTP_PROXY"),
                    OperatingSystem.IsWindows());
                // Plain http to the Ubuntu archives breaks through restrictive
                // proxies; upgrade to https, or a mirror when one is configured.
                BrowserInstaller.PrepareAptSources(
                    Environment.GetEnvironmentVariable(BrowserInstaller.AptMirrorEnv),
                    OperatingSystem.IsWindows());
            }

            try
            {
                exitCode = Microsoft.Playwright.Program.Main(step);
            }
            catch (Exception ex)
            {
                // Playwright's Main lets driver-start failures escape; left
                // uncaught they abort this process with SIGABRT (exit code
                // 134), a code the parent cannot distinguish from a hard
                // runtime crash. Fail with a plain code and the cause on
                // stderr instead.
                Console.Error.WriteLine("Playwright step \"" + step[0] + "\" failed: " + ex.Message);
                exitCode = 1;
            }

            if (exitCode != 0)
            {
                break;
            }
        }

        if (exitCode == 0 && browserName == "firefox")
        {
            // Mark the installed build with this package's version so the
            // firefox readiness check can detect a future package/build skew.
            FirefoxBrowserService.WriteFirefoxBuildMarker();
        }

        Environment.Exit(exitCode);
    }
}
