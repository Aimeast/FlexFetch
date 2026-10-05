using FlexFetch.Config;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Session;

namespace FlexFetch.Startup;

/// <summary>
/// Startup-time runtime environment: data directory layout, environment
/// variables consumed by spawned child processes, and the editable user
/// configuration file mounted into the app configuration.
/// </summary>
public static class RuntimeSetup
{
    /// <summary>
    /// Prepares the data directory and child-process environment, mounts the
    /// user configuration into <see cref="WebApplicationBuilder.Configuration"/>
    /// and returns the resolved data directory.
    /// </summary>
    public static string AddFlexFetchRuntime(this WebApplicationBuilder builder)
    {
        // Data directory: hidden runtime folder (.flexfetch) holding the database,
        // logs, browser profiles, external components and route files. Container
        // deployments mount the persistent volume at /data, which the app adopts
        // without an environment variable.
        var dataDir = ResolveDataDir(builder.Configuration);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

        // Self-managed components live under the data directory (in containers the
        // volume survives recreation, so nothing re-downloads): point Playwright's
        // browser cache into components/ms-playwright unless the deployment pinned
        // it explicitly.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FirefoxBrowserService.BrowsersPathEnv)))
        {
            Environment.SetEnvironmentVariable(
                FirefoxBrowserService.BrowsersPathEnv,
                Path.Combine(Path.GetFullPath(dataDir), "components", "ms-playwright"));
        }

        // yt-dlp resolves its JS runtime (deno - n-challenge solving and the bgutil
        // PO token script provider) through PATH alone on Linux; the frozen-binary
        // directory shortcut only exists on Windows. The data directory is never on
        // PATH in deployments, so export it here: every spawned child (yt-dlp and
        // the deno it launches for PO tokens) inherits the parent environment.
        // Applied before any component exists - an entry without files is harmless.
        Environment.SetEnvironmentVariable("PATH", YtdlpService.AppendToPathValue(
            Environment.GetEnvironmentVariable("PATH"),
            Path.Combine(Path.GetFullPath(dataDir), "components")));

        // Editable runtime configuration on the data directory: seeded from the
        // bundled template on first start and loaded last, so volume-persistent
        // user edits override appsettings*.json without touching the image. A
        // malformed user edit is moved aside (never loaded, never destroyed) and
        // re-seeded, so a typo in the config cannot keep the web server from
        // starting.
        UserConfigFile.Seed(dataDir, AppContext.BaseDirectory);
        var userConfigPath = UserConfigFile.PathFor(dataDir);
        if (!UserConfigFile.TryValidate(userConfigPath, out var configProblem) && configProblem is not null)
        {
            var aside = userConfigPath + ".invalid-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(userConfigPath, aside);
            Console.WriteLine($"User configuration {userConfigPath} is invalid ({configProblem}); moved to {aside} and re-seeded.");
            UserConfigFile.Seed(dataDir, AppContext.BaseDirectory);
        }

        // https endpoints whose certificate cannot be used are dropped from the
        // configuration (giving up https) instead of failing the whole service at
        // bind time; the http-endpoint default below only fires afterwards when no
        // endpoints survive.
        foreach (var notice in UserConfigFile.RemoveUnusableHttpsEndpoints(userConfigPath))
        {
            Console.WriteLine($"Kestrel: {notice}");
        }

        // Older or hand-written configuration files without a Kestrel section must
        // not leave the container without a listener.
        UserConfigFile.EnsureHttpEndpoint(userConfigPath);
        builder.Configuration.AddJsonFile(userConfigPath, optional: true, reloadOnChange: true);

        return dataDir;
    }

    /// <summary>
    /// Resolves the data directory: an explicit storage.dataDir wins;
    /// without one, the /data mount is adopted when it exists (container
    /// deployments map the persistent volume there - no environment
    /// variable needed), else the default hidden .flexfetch folder.
    /// </summary>
    public static string ResolveDataDir(IConfiguration configuration)
    {
        var configured = ConfigRegistry.From(configuration, ConfigKeys.DataDir);
        return configured != ConfigRegistry.GetDefault(ConfigKeys.DataDir)
            || !OperatingSystem.IsLinux()
            || !Directory.Exists("/data")
            ? configured
            : "/data";
    }
}
