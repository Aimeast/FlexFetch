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

    /// <summary>Directory holding external components (yt-dlp, deno, node, ...).</summary>
    private readonly string _componentsDir;

    /// <summary>Root directory of the managed external components.</summary>
    public string ComponentDir => _componentsDir;

    /// <summary>Per-download timeout; a slow proxy must not hang forever.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Serializes installs/upgrades so concurrent triggers (startup hosted
    /// service, lazy install before a task, browser startup) never start
    /// multiple downloads of the same component.</summary>
    private readonly SemaphoreSlim _installLock = new(1, 1);

    /// <summary>Serializes whole upgrade runs (manual button + periodic service).</summary>
    private readonly SemaphoreSlim _upgradeLock = new(1, 1);

    /// <summary>Injected latest-ffmpeg-version query (default: the upstream
    /// stable version number via gyan.dev, or the ffmpeg-static release tag
    /// on macOS); testable.</summary>
    private readonly Func<CancellationToken, Task<string?>> _fetchLatestFfmpegTag;

    /// <summary>Component currently being upgraded, or null when idle.</summary>
    private volatile string? _currentUpgradeComponent;

    /// <summary>Set after the first full component check; later calls skip logging.</summary>
    private bool _checked;

    /// <summary>
    /// Memoized version probes (see <see cref="CachedVersionAsync"/>); nulled
    /// after an install or upgrade so the next probe reflects the new binary.
    /// </summary>
    private volatile Task<string?>? _ytdlpVersion;
    private volatile Task<string?>? _denoVersion;
    private volatile Task<string?>? _ffmpegVersion;

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

    /// <summary>
    /// Appends a directory to a PATH-style value. An entry already present is
    /// kept once (case-insensitively on Windows, where paths compare without
    /// case). Pure so tests exercise it without touching process state.
    /// </summary>
    public static string AppendToPathValue(string? pathValue, string directory)
    {
        var entries = (pathValue ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (!entries.Contains(directory, comparer))
        {
            entries.Add(directory);
        }

        return string.Join(Path.PathSeparator, entries);
    }

    public string FfmpegPath => Path.Combine(_componentsDir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    /// <summary>
    /// Returns the installed yt-dlp version, or null when missing or the
    /// binary cannot run. Runs the binary: YoutubeDLSharp's Version property
    /// reads the PE version resource, which the Linux PyInstaller build does
    /// not carry, so containers always reported "not installed".
    /// Memoized: a PyInstaller single-file binary re-extracts on every run,
    /// so repeated probes cost seconds each.
    /// </summary>
    public Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
        => CachedVersionAsync(
            () => _ytdlpVersion,
            version => _ytdlpVersion = version,
            // The shared task outlives the first caller's request, so the
            // probe must not observe that request's cancellation token.
            () => QueryYtdlpVersionAsync(CancellationToken.None));

    /// <summary>
    /// Returns the installed deno version, or null when missing.
    /// Memoized for the same reason as <see cref="GetVersionAsync"/>.
    /// </summary>
    public Task<string?> GetDenoVersionAsync(CancellationToken cancellationToken = default)
        => CachedVersionAsync(
            () => _denoVersion,
            version => _denoVersion = version,
            () => QueryDenoVersionAsync(CancellationToken.None));

    /// <summary>Returns the installed ffmpeg version string, or null when missing.</summary>
    public Task<string?> GetFfmpegVersionAsync(CancellationToken cancellationToken = default)
        => CachedVersionAsync(
            () => _ffmpegVersion,
            version => _ffmpegVersion = version,
            () => QueryFfmpegVersionAsync(CancellationToken.None));

    /// <summary>
    /// Shared memoization for the version probes: the first caller starts the
    /// query and concurrent callers await the same task (single flight), so a
    /// page load and a background install never spawn duplicate processes. A
    /// null result (component missing or probe failed) is not cached, so a
    /// later call retries cheaply; install/upgrade paths reset the stored
    /// task to re-probe the new binary.
    /// Pure so tests exercise it without touching process state.
    /// </summary>
    public static async Task<string?> CachedVersionAsync(
        Func<Task<string?>?> read,
        Action<Task<string?>?> write,
        Func<Task<string?>> fresh)
    {
        var cached = read();
        if (cached is not null)
        {
            return await cached;
        }

        var task = fresh();
        write(task);
        try
        {
            var version = await task;
            // Clear only when no newer probe has replaced the stored task in
            // the meantime, so a late null never clobbers a fresh result.
            if (version is null && ReferenceEquals(read(), task))
            {
                write(null);
            }

            return version;
        }
        catch
        {
            if (ReferenceEquals(read(), task))
            {
                write(null);
            }

            throw;
        }
    }

    private async Task<string?> QueryYtdlpVersionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(BinaryPath))
        {
            return null;
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = BinaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--version");
        System.Diagnostics.Process? process = null;
        try
        {
            // A PyInstaller single-file binary re-extracts on every run:
            // bound the whole call so a stuck launch never holds the caller.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var version = line?.Trim();
            return string.IsNullOrEmpty(version) ? null : version;
        }
        catch (OperationCanceledException)
        {
            _log.Warning("yt-dlp --version did not finish in time; reporting no version");
            return null;
        }
        catch (Exception ex)
        {
            _log.Warning("yt-dlp --version could not be executed: {Message}", ex.Message);
            return null;
        }
        finally
        {
            // A timed-out process must not outlive the call.
            if (process is not null && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private async Task<string?> QueryDenoVersionAsync(CancellationToken cancellationToken)
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
        System.Diagnostics.Process? process = null;
        try
        {
            // Bound the probe: a hung launch would otherwise hold every later
            // caller waiting on the shared result.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return output.Trim();
        }
        catch (OperationCanceledException)
        {
            _log.Warning("deno --version did not finish in time; reporting no version");
            return null;
        }
        catch (Exception ex)
        {
            _log.Warning("deno --version could not be executed: {Message}", ex.Message);
            return null;
        }
        finally
        {
            // A timed-out process must not outlive the call.
            if (process is not null && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private async Task<string?> QueryFfmpegVersionAsync(CancellationToken cancellationToken)
    {
        var path = File.Exists(FfmpegPath) ? FfmpegPath : "ffmpeg";
        System.Diagnostics.Process? process = null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(path, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Same probe bound as the other components (see QueryDenoVersionAsync).
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                _log.Warning("ffmpeg -version could not be started; reporting no version");
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                // A silent null here once left the upgrade log saying
                // "(version unknown)" with nothing to explain why.
                var stderr = (await process.StandardError.ReadToEndAsync(timeout.Token)).Trim();
                _log.Warning("ffmpeg -version exited {ExitCode}: {Detail}", process.ExitCode,
                    stderr.Length == 0
                        ? "(no stderr)"
                        : string.Join(" | ", stderr.Split('\n',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(2)));
                return null;
            }

            var first = output.Split('\n').FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(first))
            {
                _log.Warning("ffmpeg -version produced no output; reporting no version");
                return null;
            }

            return first;
        }
        catch (OperationCanceledException)
        {
            _log.Warning("ffmpeg -version did not finish in time; reporting no version");
            return null;
        }
        catch (Exception ex)
        {
            _log.Warning("ffmpeg -version could not be executed: {Message}", ex.Message);
            return null;
        }
        finally
        {
            // A timed-out process must not outlive the call.
            if (process is not null && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
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
                _ytdlpVersion = null; // the fresh binary needs a fresh probe
                _log.Information("yt-dlp {Version} installed",
                    await GetVersionAsync(cancellationToken) ?? "(version unknown)");
            }

            if (!File.Exists(DenoPath))
            {
                _log.Information("Installing deno");
                await DownloadDenoAsync(cancellationToken);
                _denoVersion = null;
                _log.Information("deno {Version} installed",
                    await GetDenoVersionAsync(cancellationToken) ?? "(version unknown)");
            }

            // ffmpeg is only bundled when a system install is missing (yt-dlp
            // prefers a system ffmpeg on PATH for merging).
            if (!IsFfmpegInstalled())
            {
                _log.Information("Installing ffmpeg");
                await DownloadFfmpegAsync(cancellationToken);
                _ffmpegVersion = null;
                _log.Information("ffmpeg {Version} installed",
                    await GetFfmpegVersionAsync(cancellationToken) ?? "(version unknown)");
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

    /// <summary>Upgrades yt-dlp via its native self-update (-U), through the
    /// proxy when configured. The daily verdict uses the uniform up-to-date
    /// line; the tool's own output stays at Debug (and at Information when
    /// it is NOT a routine no-op - upgrades and annotations need detail).</summary>
    public async Task UpgradeYtDlpAsync(CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            var output = await RunComponentWithOutputAsync(BinaryPath, BuildArgs("-U"), cancellationToken);
            if (ExtractYtdlpUpToDateVersion(output) is { } version)
            {
                _log.Debug("yt-dlp -U output: {Output}", output.Trim());
                _log.Information("yt-dlp is up to date ({Version})", version);
            }
            else
            {
                _log.Information("yt-dlp -U output: {Output}", output.Trim());
            }

            _ytdlpVersion = null; // the self-update may have changed the version
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>
    /// Upgrades deno via its native upgrade command. deno does not accept a
    /// --proxy argument, so the proxy is passed through the child process
    /// environment (HTTPS_PROXY/HTTP_PROXY), like HttpGetUrl does. The daily
    /// verdict uses the uniform up-to-date line; the tool's own output stays
    /// at Debug (and at Information when it is NOT a routine no-op).</summary>
    public async Task UpgradeDenoAsync(CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            var output = await RunComponentWithOutputAsync(DenoPath, "upgrade", cancellationToken, GetProxy());
            if (ExtractDenoUpToDateVersion(output) is { } version)
            {
                _log.Debug("deno upgrade output: {Output}", output.Trim());
                _log.Information("deno is up to date ({Version})", version);
            }
            else
            {
                _log.Information("deno upgrade output: {Output}", output.Trim());
            }

            _denoVersion = null;
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>
    /// Upgrades the bundled ffmpeg by re-downloading it. The decision lives
    /// in <see cref="UpgradeFfmpegCoreAsync"/> (static, delegate-injected,
    /// unit-tested); this wrapper owns the install lock and the version memo.
    /// </summary>
    public async Task UpgradeFfmpegAsync(CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            await UpgradeFfmpegCoreAsync(
                _log,
                _fetchLatestFfmpegTag,
                getLocalVersion: () => GetFfmpegVersionAsync(CancellationToken.None),
                refreshLocalVersion: () =>
                {
                    // The memo still holds the pre-download probe; drop it so
                    // the completion log (and the next /info) reflect the new
                    // binary.
                    _ffmpegVersion = null;
                    return GetFfmpegVersionAsync(CancellationToken.None);
                },
                download: DownloadFfmpegAsync,
                cancellationToken);
        }
        finally
        {
            _installLock.Release();
        }
    }

    /// <summary>
    /// One ffmpeg upgrade round: query the latest release, skip when the
    /// installed binary is not older (the download is large and the binary
    /// source can lag the version source - see <see cref="IsFfmpegUpToDate"/>),
    /// otherwise download and re-probe so the completion log states the
    /// version actually installed. Returns true when a download happened.
    /// </summary>
    public static async Task<bool> UpgradeFfmpegCoreAsync(
        ILogger log,
        Func<CancellationToken, Task<string?>> fetchLatestVersion,
        Func<Task<string?>> getLocalVersion,
        Func<Task<string?>> refreshLocalVersion,
        Func<CancellationToken, Task> download,
        CancellationToken cancellationToken)
    {
        string? latest;
        try
        {
            latest = await fetchLatestVersion(cancellationToken);
        }
        catch (Exception ex)
        {
            // Cannot verify freshness (network/API issue): do not download
            // blindly, keep whatever is installed.
            log.Warning(ex, "Could not query latest ffmpeg version; skipping upgrade");
            return false;
        }

        if (string.IsNullOrEmpty(latest))
        {
            log.Warning("Latest ffmpeg version query returned no version; skipping upgrade");
            return false;
        }

        // Compare against the installed binary's own version string (the
        // binary is the single source of truth - no sidecar marker file).
        var localLine = await getLocalVersion();
        if (IsFfmpegUpToDate(localLine, latest))
        {
            // Uniform per-component verdict (yt-dlp/deno speak theirs too);
            // both versions appear in the Upgrading line when they differ.
            log.Information("ffmpeg is up to date ({Version})",
                ExtractFfmpegVersionNumber(localLine) ?? "unknown");
            return false;
        }

        log.Information("Upgrading ffmpeg ({Old} -> {New})",
            ExtractFfmpegVersionNumber(localLine) ?? "none", latest);
        await download(cancellationToken);

        // Log the outcome with the binary's own words: the download source
        // can serve an older build than the version query suggested (a
        // stalled release repo or a stale cache) - the re-probe makes that
        // visible instead of leaving the round silently "successful". The
        // raw line is logged when it does not parse, so a new version-string
        // flavor diagnoses itself.
        var refreshed = await refreshLocalVersion();
        var installed = ExtractFfmpegVersionNumber(refreshed);
        log.Information("ffmpeg {Version} installed",
            installed ?? refreshed ?? "(version unknown)");

        // The binary supplier rebuilds daily: a round that fires between an
        // upstream release and the supplier's rebuild installs the previous
        // patch. Make that state explicit instead of looking like a silent
        // no-op - the next round retries once the rebuild carries it.
        if (Version.TryParse(installed, out var actual)
            && Version.TryParse(ExtractReleaseVersion(latest), out var target)
            && actual < target)
        {
            log.Information("ffmpeg binary source is still at {Installed} (upstream {Target}); retrying next round",
                installed, ExtractReleaseVersion(latest));
        }

        return true;
    }

    /// <summary>
    /// True when the installed ffmpeg version line is not older than the
    /// release version (e.g. "ffmpeg version 7.1-essentials_build..." vs
    /// "7.1"). ">=" rather than "==": the binary download source can lag the
    /// version query source (a stalled release repo, a stale cache), and an
    /// installed build newer than the source's latest must not be
    /// "upgraded" - re-downloading would rewrite the same bytes every cycle.
    /// </summary>
    public static bool IsFfmpegUpToDate(string? localVersionLine, string latestVersion)
    {
        var local = ExtractFfmpegVersionNumber(localVersionLine);
        var latest = ExtractReleaseVersion(latestVersion);
        if (local is null || latest is null)
        {
            // Unrecognized input: only skip on an exact textual match, never
            // on a guess (the installed binary could be older or newer).
            return local is not null && string.Equals(local, latestVersion, StringComparison.Ordinal);
        }

        return Version.TryParse(local, out var parsedLocal) && Version.TryParse(latest, out var parsedLatest)
            ? parsedLocal >= parsedLatest
            : string.Equals(local, latest, StringComparison.Ordinal);
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
        // release version and would trigger a needless re-download). The
        // optional n/v prefix covers release-tag flavored builds - BtbN's
        // report "ffmpeg version n9.0.2-25-g67b60c310b-20261010".
        var match = System.Text.RegularExpressions.Regex.Match(versionLine, @"version\s+[nv]?([0-9]+(?:\.[0-9]+)+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Extracts "6.1.1" from a release tag/version string such as
    /// "b6.1.1", "v7.1", "n9.0" or "9.0.2", or null when none is present.</summary>
    public static string? ExtractReleaseVersion(string? versionTag)
    {
        if (string.IsNullOrWhiteSpace(versionTag))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            versionTag.Trim(), @"^[nvb]?([0-9]+(?:\.[0-9]+)+)$");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Strips ANSI color escapes so tool output can be matched
    /// reliably (deno upgrade colors its version numbers).</summary>
    public static string StripAnsi(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "\x1B\\[[0-9;]*[A-Za-z]", string.Empty);

    /// <summary>Extracts the version from yt-dlp's self-update verdict
    /// ("yt-dlp is up to date (stable@2026.08.19 from ...)"), or null when
    /// the output is not a routine no-op - upgrades and annotations need
    /// the full output at full level instead.</summary>
    public static string? ExtractYtdlpUpToDateVersion(string output)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            output, @"is up to date \((?:[a-z]+@)?([0-9]+(?:\.[0-9]+)+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Extracts the version from deno's upgrade verdict ("Local
    /// deno version 2.9.7 is the most recent release", ANSI-colored), or
    /// null when the output is not a routine no-op.</summary>
    public static string? ExtractDenoUpToDateVersion(string output)
    {
        var clean = StripAnsi(output);
        var match = System.Text.RegularExpressions.Regex.Match(
            clean, @"deno version v?([0-9]+(?:\.[0-9]+)+) is the most recent",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Returns the version of the latest stable ffmpeg release, or
    /// null. The version authority is the upstream stable number (gyan.dev's
    /// plain-text endpoint - the same number ffmpeg.org announces), NOT the
    /// binary supplier's own naming: the BtbN asset name only carries the
    /// branch ("n9.0"), which is rebuilt daily under that same name, so a
    /// patch release (9.0.2 -> 9.0.3) would be invisible there. BtbN's daily
    /// rebuilds track upstream within a day, so a round that fires inside
    /// that lag window re-installs the previous patch once and converges on
    /// the next (surfaced by the retry note in the upgrade round). macOS
    /// (dev only) keeps the ffmpeg-static tag as both its version and
    /// binary source.</summary>
    private async Task<string?> GetLatestFfmpegTagAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
        {
            // gyan.dev publishes a plain-text endpoint with the current stable
            // release version (e.g. "9.0.2"); stable builds avoid the rolling
            // master/development artifacts.
            const string url = "https://www.gyan.dev/ffmpeg/builds/release-version";
            var bytes = await DownloadBytesAsync(url, cancellationToken);
            var version = System.Text.Encoding.UTF8.GetString(bytes).Trim();
            return string.IsNullOrEmpty(version) ? null : version;
        }

        // macOS: bare release tag from the ffmpeg-static repo.
        const string macOSReleasesUrl = "https://api.github.com/repos/eugeneware/ffmpeg-static/releases/latest";
        var json = System.Text.Encoding.UTF8.GetString(await DownloadBytesAsync(macOSReleasesUrl, cancellationToken));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
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
    /// Runs a component binary with the given arguments and returns its
    /// combined output WITHOUT logging: the caller classifies it (uniform
    /// up-to-date verdict vs. full detail). When a proxy is provided it is
    /// set on the child process environment.
    /// </summary>
    private async Task<string> RunComponentWithOutputAsync(string fileName, string args, CancellationToken cancellationToken, string? proxyUrl = null)
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

        return await RunProcessWithOutputAsync(psi, cancellationToken);
    }

    /// <summary>
    /// Runs a process to completion, throws on a non-zero exit code and
    /// returns the combined output.
    /// </summary>
    public static async Task<string> RunProcessWithOutputAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken cancellationToken)
    {
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {psi.FileName}");

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(psi.FileName)} failed (exit {process.ExitCode}): {stderr.Trim()}");
        }

        // Some CLIs (e.g. deno upgrade) write their output to stderr even on
        // success, so combine both streams to avoid an empty output.
        return string.Join(Environment.NewLine,
            new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>
    /// Runs a process to completion and throws on a non-zero exit code.
    /// Shared with the session-side services (deno-based installs). When a
    /// logger is given the combined output is logged on success.
    /// </summary>
    public static async Task RunProcessAsync(System.Diagnostics.ProcessStartInfo psi, CancellationToken cancellationToken, ILogger? log = null)
    {
        var output = await RunProcessWithOutputAsync(psi, cancellationToken);
        if (log is null)
        {
            return;
        }

        log.Information("{File} {Args} output: {Output}",
            Path.GetFileName(psi.FileName),
            psi.Arguments.Length > 0 ? psi.Arguments : string.Join(' ', psi.ArgumentList),
            output.Trim());
    }

    private async Task DownloadYtDlpAsync(CancellationToken cancellationToken)
    {
        var url = YtDlpDownloadUrl(
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS());

        var bytes = await DownloadBytesAsync(url, cancellationToken);
        await File.WriteAllBytesAsync(BinaryPath, bytes, cancellationToken);
        MakeExecutable(BinaryPath);
    }

    /// <summary>
    /// yt-dlp release download URL per platform. Linux uses the standalone
    /// PyInstaller build: the bare "yt-dlp" asset is a python zipapp that
    /// needs a system python, which slim containers lack.
    /// </summary>
    public static string YtDlpDownloadUrl(bool isWindows, bool isMacOS)
    {
        const string baseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp";
        if (isWindows)
        {
            return baseUrl + ".exe";
        }

        return isMacOS ? baseUrl + "_macos" : baseUrl + "_linux";
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
        MakeExecutable(DenoPath);
    }

    private async Task DownloadFfmpegAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            // gyan.dev stable release essentials archive, which contains
            // ffmpeg-x.y-essentials_build/bin/ffmpeg.exe (the file we copy
            // out; the rest of the archive is discarded). Stable releases
            // are used instead of the rolling BtbN master builds.
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
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            await DownloadLinuxFfmpegAsync(cancellationToken);
            return;
        }

        // macOS (dev only): BtbN publishes no macOS builds, so the
        // ffmpeg-static raw binary stays the source there.
        var macOSUrl = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64
                ? "https://github.com/eugeneware/ffmpeg-static/releases/latest/download/ffmpeg-macOS-arm64"
                : "https://github.com/eugeneware/ffmpeg-static/releases/latest/download/ffmpeg-macOS-x64";
        var macOSBytes = await DownloadBytesAsync(macOSUrl, cancellationToken);
        await File.WriteAllBytesAsync(FfmpegPath, macOSBytes, cancellationToken);
        MakeExecutable(FfmpegPath);
    }

    /// <summary>
    /// Linux: BtbN FFmpeg-Builds is the static-build source the official
    /// ffmpeg.org download page recommends, and it tracks the upstream
    /// release branches (unlike the stalled ffmpeg-static repo this
    /// replaces). Its assets are tar.xz archives - .NET has no Xz decoder,
    /// so bin/ffmpeg is extracted with the system tar every deployment
    /// image carries.
    /// </summary>
    private async Task DownloadLinuxFfmpegAsync(CancellationToken cancellationToken)
    {
        var asset = await GetBtbnAssetAsync(cancellationToken);
        var archivePath = Path.Combine(Path.GetTempPath(), $"ffmpeg-{Guid.NewGuid():N}.tar.xz");
        var extractDir = Path.Combine(Path.GetTempPath(), $"ffmpeg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);
        try
        {
            var bytes = await DownloadBytesAsync(asset.Url, cancellationToken);
            await File.WriteAllBytesAsync(archivePath, bytes, cancellationToken);
            // Extract only bin/ffmpeg: the full archive also carries several
            // hundred MB of static libraries that would be discarded anyway.
            await RunTarAsync(
                $"-xJf \"{archivePath}\" -C \"{extractDir}\" --wildcards \"*/bin/ffmpeg\"",
                cancellationToken);

            var binary = Directory.EnumerateFiles(extractDir, "ffmpeg", SearchOption.AllDirectories)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(path)), "bin", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("ffmpeg archive does not contain bin/ffmpeg");
            File.Copy(binary, FfmpegPath, overwrite: true);
        }
        finally
        {
            TryDelete(archivePath);
            TryDelete(extractDir);
        }

        MakeExecutable(FfmpegPath);
    }

    /// <summary>A BtbN FFmpeg-Builds release asset selected for install: the
    /// download URL and the release-branch version its file name carries
    /// (e.g. "9.0" for ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz).</summary>
    public sealed record BtbnFfmpegAsset(string Url, string BranchVersion);

    /// <summary>
    /// Resolves the BtbN auto-build asset to install: the newest
    /// release-branch static GPL build for the running architecture. The
    /// "latest" release is rebuilt daily and its assets track both git
    /// master and the numbered release branches; master is skipped (rolling,
    /// unversioned) and shared variants are skipped (they need bundled
    /// libraries next to the binary).
    /// </summary>
    private async Task<BtbnFfmpegAsset> GetBtbnAssetAsync(CancellationToken cancellationToken)
    {
        const string releasesUrl = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";
        var json = System.Text.Encoding.UTF8.GetString(await DownloadBytesAsync(releasesUrl, cancellationToken));
        return SelectBtbnAsset(json,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64)
            ?? throw new InvalidOperationException(
                "no ffmpeg release-branch build found in the BtbN auto-build release");
    }

    /// <summary>Pure selection over a BtbN release JSON: the newest
    /// ffmpeg-nX.Y-latest-linux(64|arm64)-gpl-X.Y.tar.xz asset, or null.</summary>
    public static BtbnFfmpegAsset? SelectBtbnAsset(string json, bool isArm64)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var platform = isArm64 ? "linuxarm64" : "linux64";
        BtbnFfmpegAsset? best = null;
        var bestVersion = new Version(0, 0);
        foreach (var entry in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                name, $@"^ffmpeg-n(?<branch>[0-9]+\.[0-9]+)-latest-{platform}-gpl-[0-9]+\.[0-9]+\.tar\.xz$");
            if (!match.Success || !Version.TryParse(match.Groups["branch"].Value, out var version))
            {
                continue;
            }

            if (best is not null && version <= bestVersion)
            {
                continue;
            }

            var url = entry.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            best = new BtbnFfmpegAsset(url, match.Groups["branch"].Value);
            bestVersion = version;
        }

        return best;
    }

    /// <summary>Runs the system tar with the given arguments, bounded so a
    /// stuck extraction cannot hold the install lock forever.</summary>
    private static async Task RunTarAsync(string arguments, CancellationToken cancellationToken)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("tar", arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var timeout = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("tar could not be started");
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"tar extraction failed (exit {process.ExitCode}): {await stderr}");
        }
    }

    /// <summary>Best-effort temp cleanup; the next run uses fresh paths.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is best-effort.
        }
    }

    /// <summary>
    /// Sets the executable bit on a downloaded component binary. Without it
    /// a Unix process start fails with EACCES; a no-op on Windows where the
    /// .exe suffix carries the meaning.
    /// </summary>
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const System.IO.UnixFileMode mode = System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite
            | System.IO.UnixFileMode.UserExecute | System.IO.UnixFileMode.GroupRead
            | System.IO.UnixFileMode.GroupExecute | System.IO.UnixFileMode.OtherRead
            | System.IO.UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, mode);
    }

    /// <summary>
    /// Downloads a file through the proxy policy with a bounded timeout.
    /// The shared routing handler already follows redirects, so latest-release
    /// URLs (yt-dlp/deno) that respond with 302 are downloaded correctly.
    /// Shared with the session-side services (PO token provider installs).
    /// </summary>
    public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken)
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
