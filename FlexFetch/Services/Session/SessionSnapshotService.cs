using System.Text.Json;
using FlexFetch.Entities;

namespace FlexFetch.Services.Session;

/// <summary>
/// Metadata of the session snapshot (session.meta.json): visitor identity
/// paired with the cookie jar, plus bookkeeping for the status APIs.
/// </summary>
public sealed class SessionMeta
{
    /// <summary>Visitor identity (from the VISITOR_INFO1_LIVE cookie) that
    /// must accompany cookie-authenticated requests as visitor_data.</summary>
    public string? VisitorData { get; set; }

    public DateTime? ImportedAt { get; set; }

    public DateTime? ExportedAt { get; set; }

    /// <summary>Health verdict of the last probe/export/canary check.</summary>
    public string Health { get; set; } = SessionHealth.Unknown.ToString();

    public int CookieCount { get; set; }

    public string? LastProbeClass { get; set; }

    public string? LastError { get; set; }
}

/// <summary>Health verdicts surfaced by the session status APIs.</summary>
public enum SessionHealth
{
    Unknown,

    /// <summary>The InnerTube probe accepted the jar.</summary>
    Ok,

    /// <summary>Bot challenge - the jar is being throttled.</summary>
    BotCheck,

    /// <summary>The server rotated/invalidated the jar's tickets.</summary>
    SessionRotated,

    /// <summary>Web surface rejected but InnerTube accepted (device-bound
    /// session); informational, the jar still works for yt-dlp.</summary>
    WebBindingOnly,

    /// <summary>The jar is inconsistent; only a fresh import fixes it.</summary>
    JarInconsistent,

    /// <summary>Login required - the jar no longer carries a session.</summary>
    LoginRequired,

    /// <summary>Probe or pipeline errored (network, process, timeout).</summary>
    Error,
}

/// <summary>
/// Store of the session snapshot - the single source of truth for the
/// YouTube login session. The snapshot files are written ONLY by the session
/// export/import pipeline; yt-dlp, probes and the canary always receive a
/// random-named copy (yt-dlp rewrites the cookies file it is given, so the
/// original must never be handed to it).
/// </summary>
public sealed class SessionSnapshotService
{
    private readonly string _sessionDir;

    public SessionSnapshotService(StorageService storage)
    {
        _sessionDir = Path.Combine(storage.DataDir, "session");
        Directory.CreateDirectory(_sessionDir);
    }

    public string SnapshotPath => Path.Combine(_sessionDir, "session.txt");

    public string MetaPath => Path.Combine(_sessionDir, "session.meta.json");

    /// <summary>True when a snapshot file exists.</summary>
    public bool Exists => File.Exists(SnapshotPath);

    /// <summary>Age of the snapshot file, or null when missing.</summary>
    public TimeSpan? Age => File.Exists(SnapshotPath)
        ? DateTime.UtcNow - File.GetLastWriteTimeUtc(SnapshotPath)
        : null;

    /// <summary>Loads the snapshot cookies (empty when missing/unreadable).</summary>
    public IReadOnlyList<CookieItem> ReadCookies() => Downloaders.CookieFile.Read(SnapshotPath);

    /// <summary>Loads the snapshot metadata, or null when missing.</summary>
    public SessionMeta? ReadMeta()
    {
        try
        {
            if (!File.Exists(MetaPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SessionMeta>(File.ReadAllText(MetaPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a random-named COPY of the snapshot for exactly one yt-dlp
    /// run. The caller MUST delete the returned file when the run finishes -
    /// the copy is never merged back into the snapshot.
    /// </summary>
    public string? CreateSnapshotCopy()
    {
        if (!Exists)
        {
            return null;
        }

        var copiesDir = Path.Combine(_sessionDir, "copies");
        Directory.CreateDirectory(copiesDir);
        var copyPath = Path.Combine(copiesDir, $"session-{Guid.NewGuid():N}.txt");
        File.Copy(SnapshotPath, copyPath, overwrite: true);
        return copyPath;
    }

    /// <summary>
    /// Atomically replaces the snapshot (cookies + metadata). Callers must
    /// hold the export lock; a failed pipeline never reaches this method, so
    /// a rejected jar can never overwrite the last known good snapshot.
    /// </summary>
    public void WriteSnapshot(IReadOnlyList<CookieItem> cookies, SessionMeta meta)
    {
        Directory.CreateDirectory(_sessionDir);
        var tmp = SnapshotPath + ".tmp";
        File.WriteAllText(tmp, Downloaders.CookieFile.BuildContent(cookies));
        File.Move(tmp, SnapshotPath, overwrite: true);

        meta.CookieCount = cookies.Count;
        var metaTmp = MetaPath + ".tmp";
        File.WriteAllText(metaTmp, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(metaTmp, MetaPath, overwrite: true);
    }

    /// <summary>Updates only the metadata (e.g. canary verdicts).</summary>
    public void UpdateMeta(Action<SessionMeta> update)
    {
        var meta = ReadMeta() ?? new SessionMeta();
        update(meta);
        Directory.CreateDirectory(_sessionDir);
        var metaTmp = MetaPath + ".tmp";
        File.WriteAllText(metaTmp, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(metaTmp, MetaPath, overwrite: true);
    }

    /// <summary>
    /// Keeps only cookies whose domain belongs to YouTube (the snapshot's
    /// minimal form). Domain equality ignores a leading dot and case; any
    /// subdomain of youtube.com counts. Merging back extra domains would
    /// grow the snapshot on every browser round-trip, so they are dropped.
    /// </summary>
    public static IReadOnlyList<CookieItem> FilterYouTubeDomain(IReadOnlyList<CookieItem> cookies) =>
        cookies.Where(c => IsYouTubeDomain(c.Domain)).ToList();

    /// <summary>True when the (leading-dot-insensitive) domain is youtube.com or a subdomain.</summary>
    public static bool IsYouTubeDomain(string? domain)
    {
        var d = (domain ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return d.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || d.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Merges the incoming cookies over the existing ones. The key is the
    /// (name, domain) pair - the same name on different domains is a
    /// DIFFERENT cookie and is never merged across domains; the incoming
    /// value wins within its own key. Expired cookies are dropped.
    /// </summary>
    public static IReadOnlyList<CookieItem> Merge(IReadOnlyList<CookieItem> existing, IReadOnlyList<CookieItem> incoming)
    {
        var map = new Dictionary<(string Name, string Domain), CookieItem>();
        foreach (var c in existing)
        {
            if (!IsExpired(c))
            {
                map[Key(c)] = c;
            }
        }

        foreach (var c in incoming)
        {
            if (IsExpired(c))
            {
                map.Remove(Key(c));
                continue;
            }

            map[Key(c)] = c;
        }

        return map.Values.ToList();
    }

    private static (string, string) Key(CookieItem c) =>
        (c.Name, (c.Domain ?? string.Empty).TrimStart('.').ToLowerInvariant());

    private static bool IsExpired(CookieItem c) =>
        c.ExpiresAt is not null && c.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow;
}
