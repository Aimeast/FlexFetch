using FlexFetch.Config;
using FlexFetch.Data;
using Serilog;
using System.Diagnostics;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services;

/// <summary>
/// Manages the yt-dlp binary: locates it, installs it (through the proxy
/// policy) when missing, reports its version and upgrades it.
/// </summary>
public sealed class YoutubeDLService
{
    private readonly IProxyService _proxy;
    private readonly IConfigRepository _config;
    private readonly ILogger _log;

    /// <summary>Directory holding external components (yt-dlp, deno, ...).</summary>
    private readonly string _componentsDir;

    public YoutubeDLService(IProxyService proxy, IConfigRepository config, ILogger log, string dataDir)
    {
        _proxy = proxy;
        _config = config;
        _log = log;
        _componentsDir = Path.Combine(dataDir, "components");
        Directory.CreateDirectory(_componentsDir);
    }

    public string BinaryPath => Path.Combine(_componentsDir, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    /// <summary>Returns the installed version, or null when yt-dlp is missing.</summary>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(BinaryPath))
        {
            return null;
        }

        var psi = new ProcessStartInfo(BinaryPath, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return output.Trim();
    }

    /// <summary>Installs the latest yt-dlp when missing; returns the version.</summary>
    public async Task<string?> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(BinaryPath))
        {
            return await GetVersionAsync(cancellationToken);
        }

        await InstallLatestAsync(cancellationToken);
        return await GetVersionAsync(cancellationToken);
    }

    /// <summary>Downloads the latest yt-dlp release (through the proxy policy).</summary>
    public async Task InstallLatestAsync(CancellationToken cancellationToken = default)
    {
        var fileName = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
        var url = new Uri($"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{fileName}");

        _log.Information("Installing yt-dlp from {Url}", url);
        var handler = _proxy.CreateHandler(url);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var tmp = BinaryPath + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await stream.CopyToAsync(fs, cancellationToken);
        }

        File.Move(tmp, BinaryPath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(BinaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _log.Information("yt-dlp installed at {Path}", BinaryPath);
    }

    /// <summary>Upgrades yt-dlp to the latest version (through the proxy policy).</summary>
    public async Task UpgradeAsync(CancellationToken cancellationToken = default)
    {
        await InstallLatestAsync(cancellationToken);
    }
}
