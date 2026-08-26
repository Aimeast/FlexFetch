using System.Net;
using System.Text.Json;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Services.Routing;
using Serilog;
using YoutubeDLSharp;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Manages the external component binaries (yt-dlp, deno): locates them,
/// reports versions, and installs/upgrades them. Downloads go through the
/// configured proxy policy (the library's Utils helpers use a bare
/// HttpClient with no proxy and a fixed timeout, which stalls on restricted
/// networks), so they are implemented here instead.
/// </summary>
public sealed class YtdlpService
{
    private readonly IProxyService _proxy;
    private readonly ILogger _log;

    /// <summary>Directory holding external components (yt-dlp, deno, ...).</summary>
    private readonly string _componentsDir;

    /// <summary>Per-download timeout; a slow proxy must not hang forever.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Serializes installs/upgrades so concurrent triggers (startup hosted
    /// service, lazy install before a task, browser startup) never start
    /// multiple downloads of the same component.</summary>
    private readonly SemaphoreSlim _installLock = new(1, 1);

    /// <summary>Serializes whole upgrade runs (manual button + periodic service).</summary>
    private readonly SemaphoreSlim _upgradeLock = new(1, 1);

    /// <summary>Injected latest-ffmpeg-version query (default: GitHub API); testable.</summary>
    private readonly Func<CancellationToken, Task<string?>> _fetchLatestFfmpegTag;

    /// <summary>Component currently being upgraded, or null when idle.</summary>
    private volatile string? _currentUpgradeComponent;

    /// <summary>Set after the first full component check; later calls skip logging.</summary>
    private bool _checked;

    public YtdlpService(
        IProxyService proxy,
        ILogger log,
        string dataDir,
        Func<CancellationToken, Task<string?>>? fetchLatestFfmpegTag = null)
    {
        _proxy = proxy;
        _log = log;
        _componentsDir = Path.Combine(dataDir, "components");
        Directory.CreateDirectory(_componentsDir);
        _fetchLatestFfmpegTag = fetchLatestFfmpegTag ?? GetLatestFfmpegTagAsync;
    }

    public string BinaryPath => Path.Combine(_componentsDir, Utils.YtDlpBinaryName);

    public string DenoPath => Path.Combine(_componentsDir, OperatingSystem.IsWindows() ? "deno.exe" : "deno");

    public string FfmpegPath => Path.Combine(_componentsDir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    /// <summary>Returns the installed yt-dlp version, or null when missing.</summary>
    public string? GetVersion()
    {
        if (!File.Exists(BinaryPath))
        {
            return null;
        }

        var ytdlp = new YoutubeDL { YoutubeDLPath = BinaryPath };
        return ytdlp.Version;
    }

    /// <summary>Returns the installed yt-dlp version, or null when missing.</summary>
    public Task<string?> GetVersionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(GetVersion());

    /// <summary>Returns the installed deno version, or null when missing.</summary>
    public async Task<string?> GetDenoVersionAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DenoPath))
        {
            return null;
        }

        var psi = new System.Diagnostics.ProcessStartInfo(DenoPath, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return output.Trim();
    }

    /// <summary>True when the yt-dlp binary is present.</summary>
    public bool IsYtDlpInstalled() => File.Exists(BinaryPath);

    /// <summary>True when the deno binary is present.</summary>
    public bool IsDenoInstalled() => File.Exists(DenoPath);

    /// <summary>
    /// True when ffmpeg is available: either the bundled component or the
    /// system PATH (yt-dlp also picks up a system ffmpeg for merging).
    /// </summary>
    public bool IsFfmpegInstalled()
    {
        if (File.Exists(FfmpegPath))
        {
            return true;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(2000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns the installed ffmpeg version string, or null when missing.</summary>
    public async Task<string?> GetFfmpegVersionAsync(CancellationToken cancellationToken = default)
    {
        var path = File.Exists(FfmpegPath) ? FfmpegPath : "ffmpeg";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(path, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                return null;
            }

            var first = output.Split('\n').FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(first) ? null : first;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Installs yt-dlp and deno when missing (idempotent, serialized).
    /// Existing components are left silent - readiness is summarized by the
    /// caller (StartupTasksHostedService). Later calls - e.g. the per-task
    /// install check before using yt-dlp - are a fast no-op.
    /// </summary>
    public async Task EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            if (_checked)
            {
                return; // already verified once; components are in place
            }

            // Re-check under the lock: a concurrent call may have installed
            // the component while we were waiting.
            if (!File.Exists(BinaryPath))
            {
                _log.Information("Installing yt-dlp");
                await DownloadYtDlpAsync(cancellationToken);
            }

            if (!File.Exists(DenoPath))
            {
                _log.Information("Installing deno");
                await DownloadDenoAsync(cancellationToken);
            }

            // ffmpeg is only bundled when a system install is missing (yt-dlp
            // prefers a system ffmpeg on PATH for merging).
            if (!IsFfmpegInstalled())
            {
                _log.Information("Installing ffmpeg");
                await DownloadFfmpegAsync(cancellationToken);
            }

            // Set only after the full check succeeded, so a failed download
            // keeps the option to retry on the next call.
            _checked = true;
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>Component currently being upgraded, or null when idle.</summary>
    public string? CurrentUpgradeComponent => _currentUpgradeComponent;

    /// <summary>True while an upgrade run is in progress.</summary>
    public bool IsUpgrading => _currentUpgradeComponent is not null;

    /// <summary>Message of the last failed upgrade run, or null when none.</summary>
    public string? LastUpgradeError { get; private set; }

    /// <summary>
    /// Upgrades all components in order (yt-dlp, deno, ffmpeg), exposing the
    /// current one via <see cref="CurrentUpgradeComponent"/> so the UI can
    /// show live progress. Concurrent runs are serialized.
    /// </summary>
    public async Task UpgradeAllAsync(CancellationToken cancellationToken = default)
    {
        await _upgradeLock.WaitAsync(cancellationToken);
        try
        {
            LastUpgradeError = null;
            await UpgradeWithStatusAsync("yt-dlp", () => UpgradeYtDlpAsync(cancellationToken), cancellationToken);
            await UpgradeWithStatusAsync("deno", () => UpgradeDenoAsync(cancellationToken), cancellationToken);
            await UpgradeWithStatusAsync("ffmpeg", () => UpgradeFfmpegAsync(cancellationToken), cancellationToken);
        }
        catch (Exception ex)
        {
            LastUpgradeError = ex.Message;
            throw;
        }
        finally
        {
            _currentUpgradeComponent = null;
            _upgradeLock.Release();
        }
    }

    private async Task UpgradeWithStatusAsync(string name, Func<Task> upgrade, CancellationToken cancellationToken)
    {
        _currentUpgradeComponent = name;
        _log.Information("Upgrading component: {Component}", name);
        await upgrade();
    }

    /// <summary>Upgrades yt-dlp via its native self-update (-U), through the proxy when configured.</summary>
    public async Task UpgradeYtDlpAsync(CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            _log.Information("Upgrading yt-dlp");
            await RunProcessAsync(BinaryPath, BuildArgs("-U"), cancellationToken);
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>
    /// Upgrades deno via its native upgrade command. deno does not accept a
    /// --proxy argument, so the proxy is passed through the child process
    /// environment (HTTPS_PROXY/HTTP_PROXY), like HttpGetUrl does.
    /// </summary>
    public async Task UpgradeDenoAsync(CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            _log.Information("Upgrading deno");
            await RunProcessAsync(DenoPath, "upgrade", cancellationToken, GetProxy());
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>
    /// Upgrades the bundled ffmpeg by re-downloading it (ffmpeg has no
    /// self-upgrade command; the download is idempotent and overwrites the
    /// existing binary). Before downloading, queries the latest stable
    /// release and skips when the installed binary already matches (the
    /// archive is large, so an unconditional re-download would be wasteful).
    /// </summary>
    public async Task UpgradeFfmpegAsync(CancellationToken cancellationToken = default)
    {
        string? latest;
        try
        {
            latest = await _fetchLatestFfmpegTag(cancellationToken);
        }
        catch (Exception ex)
        {
            // Cannot verify freshness (network/API issue): do not download
            // blindly, keep whatever is installed.
            _log.Warning(ex, "Could not query latest ffmpeg version; skipping upgrade");
            return;
        }

        if (string.IsNullOrEmpty(latest))
        {
            _log.Warning("Latest ffmpeg version query returned no version; skipping upgrade");
            return;
        }

        // Compare against the installed binary's own version string (the
        // binary is the single source of truth - no sidecar marker file).
        var localLine = await GetFfmpegVersionAsync(cancellationToken);
        if (IsFfmpegUpToDate(localLine, latest))
        {
            _log.Information("ffmpeg is already up to date ({Version})", latest);
            return;
        }

        await _installLock.WaitAsync(cancellationToken);
        try
        {
            _log.Information("Upgrading ffmpeg ({Old} -> {New})", ExtractFfmpegVersionNumber(localLine) ?? "none", latest);
            await DownloadFfmpegAsync(cancellationToken);
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>
    /// True when the installed ffmpeg version line already matches the given
    /// release version (e.g. "ffmpeg version 7.1-essentials_build..." vs "7.1").
    /// </summary>
    public static bool IsFfmpegUpToDate(string? localVersionLine, string latestVersion)
    {
        var local = ExtractFfmpegVersionNumber(localVersionLine);
        return local is not null && string.Equals(local, latestVersion, StringComparison.Ordinal);
    }

    /// <summary>Extracts "9.0.1" from "ffmpeg version 9.0.1-essentials_build...", or null.</summary>
    public static string? ExtractFfmpegVersionNumber(string? versionLine)
    {
        if (string.IsNullOrWhiteSpace(versionLine))
        {
            return null;
        }

        // Match the full version number (major.minor[.patch...]) so e.g.
        // 9.0.1 is not truncated to 9.0 (which would never equal the remote
        // release version and would trigger a needless re-download).
        var match = System.Text.RegularExpressions.Regex.Match(versionLine, @"version\s+([0-9]+(?:\.[0-9]+)+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Returns the version of the latest stable ffmpeg release, or null.</summary>
    private async Task<string?> GetLatestFfmpegTagAsync(CancellationToken cancellationToken = default)
    {
        // gyan.dev publishes a plain-text endpoint with the current stable
        // release version (e.g. "7.1"); stable builds avoid the rolling
        // master/development artifacts.
        const string url = "https://www.gyan.dev/ffmpeg/builds/release-version";
        var bytes = await DownloadBytesAsync(url, cancellationToken);
        var version = System.Text.Encoding.UTF8.GetString(bytes).Trim();
        return string.IsNullOrEmpty(version) ? null : version;
    }

    /// <summary>
    /// Returns the proxy URL for the component download host, or null.
    /// </summary>
    private string? GetProxy() =>
        _proxy.GetProxyUri(new Uri("https://github.com"));

    /// <summary>
    /// Builds command arguments and appends --proxy when the configured proxy
    /// applies to the component download host (yt-dlp supports --proxy).
    /// </summary>
    private string BuildArgs(string command)
    {
        var proxy = GetProxy();
        return proxy is null ? command : $"{command} --proxy {proxy}";
    }

    /// <summary>
    /// Runs a component binary with the given arguments and logs its output.
    /// When a proxy is provided it is set on the child process environment.
    /// </summary>
    private async Task RunProcessAsync(string fileName, string args, CancellationToken cancellationToken, string? proxyUrl = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            psi.Environment["HTTPS_PROXY"] = proxyUrl;
            psi.Environment["HTTP_PROXY"] = proxyUrl;
        }

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} {args} failed: {stderr.Trim()}");
        }

        // Some CLIs (e.g. deno upgrade) write their output to stderr even on
        // success, so log both streams to avoid an empty "output:" line.
        var output = string.Join(Environment.NewLine,
            new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
        _log.Information("{File} {Args} output: {Output}", Path.GetFileName(fileName), args, output.Trim());
    }

    private async Task DownloadYtDlpAsync(CancellationToken cancellationToken)
    {
        const string baseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp";
        var url = OperatingSystem.IsWindows() ? baseUrl + ".exe"
            : OperatingSystem.IsMacOS() ? baseUrl + "_macos"
            : baseUrl;

        var bytes = await DownloadBytesAsync(url, cancellationToken);
        await File.WriteAllBytesAsync(BinaryPath, bytes, cancellationToken);
    }

    private async Task DownloadDenoAsync(CancellationToken cancellationToken)
    {
        // deno distributes a zip archive; extract the single binary.
        const string baseUrl = "https://github.com/denoland/deno/releases/latest/download/deno";
        var url = OperatingSystem.IsWindows() ? baseUrl + "-x86_64-pc-windows-msvc.zip"
            : OperatingSystem.IsLinux() ? baseUrl + "-x86_64-unknown-linux-gnu.zip"
            : baseUrl + "-x86_64-apple-darwin.zip";

        var bytes = await DownloadBytesAsync(url, cancellationToken);
        using var stream = new MemoryStream(bytes);
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => !e.FullName.EndsWith("/", StringComparison.Ordinal));
        if (entry is null)
        {
            throw new InvalidOperationException("deno archive is empty");
        }

        using var target = File.Create(DenoPath);
        await using var source = entry.Open();
        await source.CopyToAsync(target, cancellationToken);
    }

    private async Task DownloadFfmpegAsync(CancellationToken cancellationToken)
    {
        // Windows: gyan.dev stable release essentials archive, which contains
        // ffmpeg-x.y-essentials_build/bin/ffmpeg.exe (the file we copy out;
        // the rest of the archive is discarded). Stable releases are used
        // instead of the rolling BtbN master builds.
        const string url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        var bytes = await DownloadBytesAsync(url, cancellationToken);
        using var stream = new MemoryStream(bytes);
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(Path.GetFileName(e.FullName), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new InvalidOperationException("ffmpeg archive does not contain ffmpeg.exe");
        }

        using var target = File.Create(FfmpegPath);
        await using var source = entry.Open();
        await source.CopyToAsync(target, cancellationToken);
    }

    /// <summary>
    /// Downloads a file through the proxy policy with a bounded timeout.
    /// The shared routing handler already follows redirects, so latest-release
    /// URLs (yt-dlp/deno) that respond with 302 are downloaded correctly.
    /// </summary>
    private async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        using var handler = _proxy.CreateHandler(uri);
        using var client = new HttpClient(handler)
        {
            Timeout = DownloadTimeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FlexFetch/1.0");

        _log.Information("Downloading {Url} via proxy {Proxy}", url, _proxy.GetProxyUri(uri) ?? "direct");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}
