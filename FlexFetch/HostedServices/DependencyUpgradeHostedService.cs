using FlexFetch.Config;
using FlexFetch.Services.Downloaders;
using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// Periodic component upgrade service: each round upgrades yt-dlp, deno and
/// ffmpeg (gated by ops.autoUpgradeEnabled), but only components whose last
/// successful check is older than ops.upgradePeriodHours. The freshness
/// reference is the per-component record in the components state file,
/// falling back to the binary's last-write time (fresh installs, lost
/// state). The first round runs right after startup, so a redeployed site
/// catches up on overdue components immediately instead of a full period
/// later. A missing binary is never due: the startup install owns first
/// downloads (StartupTasksHostedService). The manual upgrade button bypasses
/// this schedule entirely.
/// </summary>
public sealed class DependencyUpgradeHostedService : IntervalHostedService
{
    /// <summary>Per-component last-upgrade records live in the components
    /// directory: the persistent volume survives container redeploys, so the
    /// update cadence survives them too.</summary>
    public const string StateFileName = "upgrade-state.json";

    private readonly IConfiguration _config;
    private readonly YtdlpService _ytdlp;
    private readonly StartupTasksHostedService _startup;
    private readonly ILogger _log;

    public DependencyUpgradeHostedService(
        IConfiguration config,
        YtdlpService ytdlp,
        StartupTasksHostedService startup,
        IHostApplicationLifetime lifetime,
        ILogger log)
        : base(lifetime, log, "DependencyUpgrade")
    {
        _config = config;
        _ytdlp = ytdlp;
        _startup = startup;
        _log = log;
    }

    // The first round runs right after startup; the per-component age gate
    // decides what is actually due, so an early round is cheap when nothing
    // is.
    protected override bool RunImmediately => true;

    protected override TimeSpan GetInterval()
    {
        var hours = int.TryParse(Get(ConfigKeys.UpgradeHours), out var h) && h > 0 ? h : 24;
        return TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteOnceAsync(CancellationToken cancellationToken)
    {
        // The startup install owns first downloads and logs the component
        // plan; wait for it so an upgrade can never start (or log) before
        // that plan, matching the other background services.
        await _startup.WhenComponentPlanLogged.WaitAsync(cancellationToken);

        if (!bool.TryParse(Get(ConfigKeys.AutoUpgrade), out var autoUpgrade) || !autoUpgrade)
        {
            return;
        }

        await RunUpgradeRoundAsync(
            new (string Name, string BinaryPath, Func<Task> Upgrade)[]
            {
                ("yt-dlp", _ytdlp.BinaryPath, () => _ytdlp.UpgradeYtDlpAsync(cancellationToken)),
                ("deno", _ytdlp.DenoPath, () => _ytdlp.UpgradeDenoAsync(cancellationToken)),
                ("ffmpeg", _ytdlp.FfmpegPath, () => _ytdlp.UpgradeFfmpegAsync(cancellationToken)),
            },
            Path.Combine(_ytdlp.ComponentDir, StateFileName),
            GetInterval(),
            DateTime.UtcNow,
            due: component => _log.Information("Auto upgrade due: {Component}", component),
            failure: (component, ex) => _log.Warning(
                ex, "{Component} upgrade failed; it will retry next round", component));
    }

    /// <summary>One upgrade round: upgrade every component whose freshness
    /// reference (persisted record, falling back to the binary's last-write
    /// time) is at least one interval old, recording each success in the
    /// state file. Static and delegate-injected for unit tests. Failures
    /// are isolated per component - one broken upgrade never blocks the
    /// others, and an unrecorded failure is naturally retried next
    /// round.</summary>
    public static async Task RunUpgradeRoundAsync(
        IReadOnlyList<(string Name, string BinaryPath, Func<Task> Upgrade)> components,
        string statePath,
        TimeSpan interval,
        DateTime utcNow,
        Action<string> due,
        Action<string, Exception> failure)
    {
        var records = LoadLastUpgradeRecords(statePath);
        foreach (var component in components)
        {
            var binaryModified = File.Exists(component.BinaryPath)
                ? (DateTime?)File.GetLastWriteTimeUtc(component.BinaryPath)
                : null;
            var recorded = records.TryGetValue(component.Name, out var when)
                ? (DateTime?)when
                : null;
            if (!IsUpgradeDue(
                    binaryModified is not null, recorded, binaryModified, utcNow, interval))
            {
                continue;
            }

            due(component.Name);
            try
            {
                await component.Upgrade();
            }
            catch (Exception ex)
            {
                failure(component.Name, ex);
                continue;
            }

            records[component.Name] = utcNow;
            SaveLastUpgradeRecords(statePath, records);
        }
    }

    /// <summary>True when the component's binary exists and its freshness
    /// reference (persisted last-upgrade record, falling back to the
    /// binary's own last-write time) is at least one interval old. The
    /// boundary is inclusive: rounds are spaced by the same interval, so a
    /// strict comparison would systematically skip the round right after a
    /// check. A missing binary is never due - the startup install owns
    /// first downloads; a present binary with no reference at all counts as
    /// due.</summary>
    public static bool IsUpgradeDue(
        bool binaryExists,
        DateTime? lastUpgradeUtc,
        DateTime? binaryModifiedUtc,
        DateTime utcNow,
        TimeSpan interval)
    {
        if (!binaryExists)
        {
            return false;
        }

        var reference = lastUpgradeUtc ?? binaryModifiedUtc;
        return reference is null || utcNow - reference.Value >= interval;
    }

    /// <summary>Reads the per-component last-upgrade records (component
    /// name to UTC timestamp). A missing or unreadable file yields an empty
    /// map, letting the binary's last-write time carry the first decision
    /// after a redeploy.</summary>
    public static Dictionary<string, DateTime> LoadLastUpgradeRecords(string path)
    {
        Dictionary<string, string>? raw;
        try
        {
            raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(path));
        }
        catch (Exception)
        {
            return new Dictionary<string, DateTime>();
        }

        var records = new Dictionary<string, DateTime>();
        if (raw is null)
        {
            return records;
        }

        foreach (var (name, value) in raw)
        {
            if (DateTime.TryParse(
                    value,
                    null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var when))
            {
                records[name] = DateTime.SpecifyKind(when, DateTimeKind.Utc);
            }
        }

        return records;
    }

    /// <summary>Writes the per-component last-upgrade records as UTC ISO
    /// timestamps.</summary>
    public static void SaveLastUpgradeRecords(string path, Dictionary<string, DateTime> records)
    {
        var raw = records.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString("O"));
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(raw));
    }

    private string Get(string key) => ConfigRegistry.From(_config, key);
}
