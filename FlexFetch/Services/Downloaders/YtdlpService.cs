using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Services.Routing;
using Serilog;
using YoutubeDLSharp;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Manages the external component binaries (yt-dlp, deno): locates them,
/// reports versions, and installs/upgrades them through YoutubeDLSharp's own
/// Utils.DownloadYtDlp / Utils.DownloadDeno helpers rather than
/// re-implementing the download by hand.
/// </summary>
public sealed class YtdlpService
{
    private readonly ILogger _log;

    /// <summary>Directory holding external components (yt-dlp, deno, ...).</summary>
    private readonly string _componentsDir;

    public YtdlpService(IProxyService proxy, IConfigRepository config, ILogger log, string dataDir)
    {
        _log = log;
        _componentsDir = Path.Combine(dataDir, "components");
        Directory.CreateDirectory(_componentsDir);
    }

    public string BinaryPath => Path.Combine(_componentsDir, Utils.YtDlpBinaryName);

    public string DenoPath => Path.Combine(_componentsDir, OperatingSystem.IsWindows() ? "deno.exe" : "deno");

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

    /// <summary>Installs yt-dlp and deno (through YoutubeDLSharp's download helpers) when missing.</summary>
    public async Task EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(BinaryPath))
        {
            _log.Information("Installing yt-dlp via YoutubeDLSharp");
            await Utils.DownloadYtDlp(_componentsDir);
        }

        if (!File.Exists(DenoPath))
        {
            _log.Information("Installing deno via YoutubeDLSharp");
            await Utils.DownloadDeno(_componentsDir);
        }
    }

    /// <summary>Downloads the latest yt-dlp (through YoutubeDLSharp's own helper).</summary>
    public async Task UpgradeYtDlpAsync(CancellationToken cancellationToken = default)
    {
        _log.Information("Upgrading yt-dlp via YoutubeDLSharp");
        await Utils.DownloadYtDlp(_componentsDir);
    }

    /// <summary>Downloads the latest deno (through YoutubeDLSharp's own helper).</summary>
    public async Task UpgradeDenoAsync(CancellationToken cancellationToken = default)
    {
        _log.Information("Upgrading deno via YoutubeDLSharp");
        await Utils.DownloadDeno(_componentsDir);
    }
}
