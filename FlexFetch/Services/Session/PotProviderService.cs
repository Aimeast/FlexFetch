using System.IO.Compression;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Session;

/// <summary>
/// PO token supply stack, script mode: yt-dlp (2025.05+) knows the bgutil
/// provider natively and spawns <c>generate_once.ts</c> through Deno when a
/// PO token is needed - no long-running server, no port, no supervision and
/// no Node.js. This service keeps the pieces installed: the bgutil source
/// archive of the pinned tag (trimmed to the script-mode files, dependencies
/// installed by <c>deno install</c>) and the matching yt-dlp plugin.
/// Version pinning is by constants - upgrading means changing the constants
/// and re-running the install.
/// </summary>
public sealed class PotProviderService
{
    /// <summary>Pinned bgutil-ytdlp-pot-provider version (source + plugin).</summary>
    public const string PotProviderVersion = "2.0.0";

    /// <summary>Budget for the warm-up run: a cold deno compiles the
    /// script's whole import graph (jsdom and friends) - on low-power hosts
    /// far beyond the plugin's 15s probe timeout, which is exactly what this
    /// run absorbs.</summary>
    public static readonly TimeSpan WarmUpTimeout = TimeSpan.FromSeconds(120);

    /// <summary>GitHub repository of the server sources (the release page
    /// only ships the yt-dlp plugin zip; the script runs from the source).</summary>
    public const string ServerRepoUrl = "https://github.com/Brainicism/bgutil-ytdlp-pot-provider";

    private readonly YtdlpService _ytdlp;
    private readonly IProxyService _proxy;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task>? _warmUpRunner;
    private int _warmUpDone;

    public PotProviderService(
        YtdlpService ytdlp,
        IProxyService proxy,
        ILogger log,
        Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task>? warmUpRunner = null)
    {
        _ytdlp = ytdlp;
        _proxy = proxy;
        _log = log;
        _warmUpRunner = warmUpRunner;
    }

    private string ComponentsDir => _ytdlp.ComponentDir;

    private string DenoPath => _ytdlp.DenoPath;

    /// <summary>The bgutil source tree (script + npm dependencies).</summary>
    private string ServerRoot => Path.Combine(ComponentsDir, "bgutil-ytdlp-pot-provider");

    /// <summary>Absolute path of the one-shot token generation script the
    /// plugin spawns through Deno.</summary>
    public string ScriptPath => Path.Combine(
        ServerRoot, "server", "src", "generate_once.ts");

    /// <summary>Extractor-args fragment pointing the bgutil script-deno
    /// provider at our source tree (a separate extractor key: joining it
    /// into the youtube: args would make it an unknown sub-arg).</summary>
    public string ScriptPathArg => $"youtubepot-bgutilscript:script_path={ScriptPath}";

    /// <summary>yt-dlp loads plugins from yt-dlp-plugins next to its binary;
    /// each plugin lives in a wrapper directory of any name.</summary>
    public string PluginsDir => Path.Combine(ComponentsDir, "yt-dlp-plugins");

    /// <summary>The script's npm manifest. Deno refuses the script's bare
    /// npm imports (e.g. "commander") without it, so a tree missing this
    /// file can never run the script even with node_modules in place.</summary>
    private string PackageJsonPath => Path.Combine(ServerRoot, "server", "package.json");

    /// <summary>True when the source tree is complete enough to run: the
    /// script AND its package.json. Requiring the manifest makes a damaged
    /// tree (script present, manifest lost) count as not installed, so the
    /// supervisor re-downloads the source instead of silently never
    /// generating a token.</summary>
    public bool IsServerInstalled => File.Exists(ScriptPath) && File.Exists(PackageJsonPath);

    public bool IsDenoInstalled => File.Exists(DenoPath);

    public bool IsPluginInstalled => Directory.Exists(Path.Combine(PluginsDir, "bgutil-ytdlp-potprovider"));

    public string? LastError { get; private set; }

    /// <summary>
    /// Supervisor step: installs missing pieces (deno comes with the shared
    /// component install) and pre-warms deno for the token script.
    /// Idempotent, serialized; failures are logged and retried on the next
    /// round.
    /// </summary>
    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureInstalledAsync(cancellationToken);
            await WarmUpDenoAsync(cancellationToken);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Warning(ex, "PO token provider supervision failed");
        }
    }

    /// <summary>Installs deno, the source tree with dependencies and the
    /// yt-dlp plugin. Idempotent, serialized.</summary>
    public async Task EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            // Deno doubles as yt-dlp's JS challenge runtime; the shared
            // component install manages it (idempotent after first check).
            await _ytdlp.EnsureInstalledAsync(cancellationToken);

            if (!IsServerInstalled)
            {
                await DownloadServerSourceAsync(cancellationToken);
            }

            var depsMarker = Path.Combine(ServerRoot, "server", ".deno-deps-ok");
            var depsCurrent = File.Exists(depsMarker)
                && File.ReadAllText(depsMarker).Trim() == PotProviderVersion;
            if (!Directory.Exists(Path.Combine(ServerRoot, "server", "node_modules")) || !depsCurrent)
            {
                await InstallServerDepsAsync(cancellationToken);
            }

            if (!IsPluginInstalled)
            {
                await InstallPluginAsync(cancellationToken);
            }
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>
    /// Runs the bgutil script once through deno with the plugin's exact
    /// arguments so deno's on-disk compile cache is hot before the first
    /// real PO token request. The plugin probes the script with a hard 15s
    /// timeout (getpot_bgutil_script._GET_SCRIPT_VSN_TIMEOUT); a cold deno
    /// on a low-power host exceeds it, the probe then reports the provider
    /// unavailable and YouTube loses its PO token even though every
    /// component is fine. Runs once per process on success - a failed run
    /// is retried by the next supervisor round. Start, finish and failure
    /// are all logged so the warm-up is visible in the boot log.
    /// </summary>
    public async Task WarmUpDenoAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _warmUpDone) == 1 || !IsServerInstalled || !IsDenoInstalled)
        {
            return;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        _log.Information(
            "Pre-warming deno for the PO token script (a cold start exceeds the plugin's 15s probe budget on slow hosts)");
        try
        {
            var psi = BuildWarmUpProcess();
            if (_warmUpRunner is not null)
            {
                await _warmUpRunner(psi, cancellationToken);
            }
            else
            {
                await RunWarmUpProcessAsync(psi, cancellationToken);
            }

            Volatile.Write(ref _warmUpDone, 1);
            _log.Information("Deno pre-warm finished in {Seconds:F1}s", started.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning("Deno pre-warm failed after {Seconds:F1}s: {Message}",
                started.Elapsed.TotalSeconds, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// The command the warm-up runs - a faithful copy of the script-deno
    /// provider's probe (getpot_bgutil_script.py): same permission flags,
    /// cache directory derivation and environment, so this run populates
    /// exactly the on-disk cache the plugin's later probes hit.
    /// </summary>
    private System.Diagnostics.ProcessStartInfo BuildWarmUpProcess()
    {
        var nodeModules = Path.Combine(ServerRoot, "server", "node_modules");
        var cacheDir = ScriptCacheDir();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = DenoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--allow-env");
        psi.ArgumentList.Add("--allow-net");
        psi.ArgumentList.Add($"--allow-ffi={EscapeAllowList(nodeModules)}");
        psi.ArgumentList.Add($"--allow-write={EscapeAllowList(cacheDir)}");
        psi.ArgumentList.Add($"--allow-read={EscapeAllowList(cacheDir, nodeModules)}");
        psi.ArgumentList.Add(ScriptPath);
        psi.ArgumentList.Add("--version");
        psi.Environment["DENO_NO_PROMPT"] = "1";
        psi.Environment["DENO_NO_UPDATE_CHECK"] = "1";
        psi.Environment["FORCE_COLOR"] = "false";
        return psi;
    }

    /// <summary>The script cache directory the plugin passes to deno: under
    /// XDG_CACHE_HOME when set, else ~/.cache (the server code accepts HOME
    /// and USERPROFILE regardless of OS), else the server tree.</summary>
    private string ScriptCacheDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return Path.Combine(xdg, "bgutil-ytdlp-pot-provider");
        }

        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, ".cache", "bgutil-ytdlp-pot-provider");
        }

        return ServerRoot;
    }

    /// <summary>The plugin joins allow-list paths with commas and escapes
    /// literal commas by doubling them.</summary>
    private static string EscapeAllowList(params string[] paths) =>
        string.Join(",", paths.Select(p => p.Replace(",", ",,")));

    private static async Task RunWarmUpProcessAsync(
        System.Diagnostics.ProcessStartInfo psi,
        CancellationToken cancellationToken)
    {
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {psi.FileName}");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(WarmUpTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"deno pre-warm ran past {WarmUpTimeout.TotalSeconds:F0}s; process tree killed");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"deno pre-warm failed (exit {process.ExitCode}): {(await stderr).Trim()}");
        }
    }

    /// <summary>
    /// Downloads the pinned tag's source archive over plain HTTP (no git
    /// needed), extracts it and trims everything the script mode never loads,
    /// so a fresh install matches the minimal footprint.
    /// </summary>
    private async Task DownloadServerSourceAsync(CancellationToken cancellationToken)
    {
        var archiveUrl = $"{ServerRepoUrl}/archive/refs/tags/{PotProviderVersion}.zip";
        var zip = await _ytdlp.DownloadBytesAsync(archiveUrl, cancellationToken);

        Directory.CreateDirectory(ComponentsDir);
        var extractDir = Path.Combine(ComponentsDir, $"pot-src-{PotProviderVersion}");
        if (Directory.Exists(extractDir))
        {
            Directory.Delete(extractDir, recursive: true);
        }

        using (var stream = new MemoryStream(zip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            // GitHub source archives extract into <repo>-<version>/.
            archive.ExtractToDirectory(extractDir, overwriteFiles: true);
        }

        var extracted = Path.Combine(extractDir, $"bgutil-ytdlp-pot-provider-{PotProviderVersion}");
        if (Directory.Exists(ServerRoot))
        {
            // A failed earlier attempt may have left a partial tree.
            Directory.Delete(ServerRoot, recursive: true);
        }

        Directory.Move(extracted, ServerRoot);
        Directory.Delete(extractDir);
        TrimSource();
        _log.Information("bgutil source {Version} installed at {Root}", PotProviderVersion, ServerRoot);
    }

    /// <summary>
    /// Paths removed after extraction so the install matches the minimal
    /// script-mode footprint. deno.json is dropped deliberately: it only
    /// whitelists npm lifecycle scripts for two phantom native dependencies
    /// (canvas, @swc/core) that nothing imports - their postinstall scripts
    /// fetch prebuilt binaries from GitHub and hang without a deno-usable
    /// proxy, while the token script works without them built.
    /// </summary>
    private static readonly string[] SourceTrimPaths =
    {
        ".github",
        ".devcontainer",
        "CODEOWNERS",
        "CONTRIBUTING.md",
        "README.md",
        "install_plugin_dev.sh",
        "plugin",
        "server/deno.json",
        "server/build",
        "server/scripts",
        "server/.prettierrc.json",
        "server/eslint.config.mjs",
        "server/Dockerfile",
        "server/.gitattributes",
        "server/README.md",
        "server/tsconfig.json",
        "server/package-lock.json",
        "server/types",
        "server/src/main.ts",
    };

    private void TrimSource()
    {
        foreach (var relative in SourceTrimPaths)
        {
            var path = Path.Combine(ServerRoot, Path.Combine(relative.Split('/')));
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Installs the script's npm dependencies with <c>deno install</c> (deno
    /// populates node_modules itself; no Node.js is involved). The project's
    /// proxy policy decides how the pinned npmmirror registry is reached: an
    /// http(s) proxy is passed to the child process env (the Firefox
    /// installer proves the node-family handles this fine), while a socks
    /// primary is dropped by <see cref="InstallerProxyEnv"/> and the install
    /// runs direct - the .npmrc pin below is registry-independent (deno.lock
    /// holds tarball digests), so both paths work. A version marker skips
    /// re-resolution once the tree is in place; deleting node_modules (or
    /// the marker) triggers a reinstall.
    /// </summary>
    private async Task InstallServerDepsAsync(CancellationToken cancellationToken)
    {
        var serverDir = Path.Combine(ServerRoot, "server");
        File.WriteAllText(Path.Combine(serverDir, ".npmrc"), "registry=https://registry.npmmirror.com/\n");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = DenoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = serverDir,
        };
        psi.ArgumentList.Add("install");
        psi.Environment["DENO_NO_UPDATE_CHECK"] = "1";
        // Route the install like every other component: the configured proxy
        // policy applies (an unreachable direct route must not hang the
        // install on hosts without direct egress).
        var installerEnv = InstallerProxyEnv.Resolve(_proxy, "https://registry.npmmirror.com/");
        foreach (var (key, value) in installerEnv)
        {
            psi.Environment[key] = value;
        }

        _log.Information("Installing bgutil script dependencies via deno install via {Proxy} (npmmirror registry)",
            InstallerProxyEnv.Describe(installerEnv));
        await YtdlpService.RunProcessAsync(psi, cancellationToken);
        File.WriteAllText(Path.Combine(serverDir, ".deno-deps-ok"), PotProviderVersion);
    }

    /// <summary>Downloads and unpacks the yt-dlp plugin of the pinned version;
    /// the zip root is yt_dlp_plugins/... and must land inside a wrapper
    /// directory of any name for yt-dlp to discover it.</summary>
    private async Task InstallPluginAsync(CancellationToken cancellationToken)
    {
        var url = $"https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/{PotProviderVersion}/bgutil-ytdlp-pot-provider.zip";
        var zip = await _ytdlp.DownloadBytesAsync(url, cancellationToken);
        var wrapper = Path.Combine(PluginsDir, "bgutil-ytdlp-potprovider");
        Directory.CreateDirectory(wrapper);

        using var stream = new MemoryStream(zip);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(wrapper, overwriteFiles: true);
        _log.Information("Installed bgutil yt-dlp plugin {Version} into {Wrapper}", PotProviderVersion, wrapper);
    }
}
