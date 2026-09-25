using System.IO.Compression;
using FlexFetch.Services.Downloaders;
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

    /// <summary>GitHub repository of the server sources (the release page
    /// only ships the yt-dlp plugin zip; the script runs from the source).</summary>
    public const string ServerRepoUrl = "https://github.com/Brainicism/bgutil-ytdlp-pot-provider";

    private readonly YtdlpService _ytdlp;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public PotProviderService(
        YtdlpService ytdlp,
        ILogger log)
    {
        _ytdlp = ytdlp;
        _log = log;
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

    /// <summary>True when the source tree with the script is present.</summary>
    public bool IsServerInstalled => File.Exists(ScriptPath);

    public bool IsDenoInstalled => File.Exists(DenoPath);

    public bool IsPluginInstalled => Directory.Exists(Path.Combine(PluginsDir, "bgutil-ytdlp-potprovider"));

    public string? LastError { get; private set; }

    /// <summary>
    /// Supervisor step: installs missing pieces (deno comes with the shared
    /// component install). Idempotent, serialized; failures are logged and
    /// retried on the next round.
    /// </summary>
    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureInstalledAsync(cancellationToken);
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
    /// populates node_modules itself; no Node.js is involved). deno's own
    /// proxy networking proved unreliable in every mode (socks env and
    /// http-proxy env both stall mid-install), so the install runs DIRECT
    /// against the npmmirror registry pinned via .npmrc (the integrity
    /// hashes in deno.lock are tarball digests, registry-independent). A
    /// version marker skips re-resolution once the tree is in place;
    /// deleting node_modules (or the marker) triggers a reinstall.
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
        // Deliberately NO proxy env here, even when network.httpProxy is
        // configured: deno's proxy networking (socks and http-proxy env
        // alike) stalled mid-install in practice, while npmmirror is
        // directly reachable and fast. The .npmrc pin above is the reliable
        // path; node_modules linking after download takes a while on
        // Windows and is expected.

        _log.Information("Installing bgutil script dependencies via deno install (npmmirror registry)");
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
