using System.Text;
using FlexFetch.Services;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// File naming rules: strip illegal characters, cap length (UTF-8 aware),
/// infer names from URLs, and keep names unique within a task.
/// </summary>
public static class FileNameRules
{
    private const int MaxNameBytes = 180;

    // Windows-invalid characters enforced on EVERY platform: files land on
    // volumes users browse through SMB, where ':', '?', '|' and friends are
    // illegal even though the Linux host accepts them - Path.GetInvalidFileNameChars
    // is platform-dependent (Linux: only '/' and NUL) and would let them
    // through, leaving Windows clients with mangled 8.3 short-name aliases.
    private static readonly char[] IllegalChars = Path.GetInvalidFileNameChars()
        .Concat(new[] { '<', '>', ':', '"', '|', '?', '*' })
        .Distinct()
        .ToArray();

    /// <summary>Replaces illegal characters and trims, preserving the extension.</summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "download";
        }

        var cleaned = new string(name.Select(c => IllegalChars.Contains(c) ? '_' : c).ToArray()).Trim();

        // Cap total UTF-8 length; keep the extension if possible.
        var bytes = Encoding.UTF8.GetByteCount(cleaned);
        if (bytes <= MaxNameBytes)
        {
            return cleaned;
        }

        var extension = Path.GetExtension(cleaned);
        var stem = cleaned[..^extension.Length];
        while (Encoding.UTF8.GetByteCount(stem) + Encoding.UTF8.GetByteCount(extension) > MaxNameBytes && stem.Length > 1)
        {
            stem = stem[..^1];
        }

        return stem + extension;
    }

    /// <summary>Makes a name unique against existing names (case-insensitive): "name (2).ext".</summary>
    public static string EnsureUnique(string name, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        var stem = name[..^extension.Length];
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Infers a filename from a URL path (last segment), falling back to the host.</summary>
    public static string InferFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "download";
        }

        var segment = Uri.UnescapeDataString(uri.LocalPath.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty);
        return string.IsNullOrWhiteSpace(segment) ? uri.Host : segment;
    }

    /// <summary>
    /// Picks the display name for a direct link: the server-advertised name
    /// (Content-Disposition) when the probe saw one, else the URL path
    /// segment - extended from the probed Content-Type when the segment has
    /// no extension (share-style endpoints like "/file?taskId=..." would
    /// otherwise name every download after their literal last segment).
    /// </summary>
    public static string FromUrlAndProbe(string url, string? contentType, string? dispositionName)
    {
        if (!string.IsNullOrWhiteSpace(dispositionName))
        {
            return Sanitize(dispositionName);
        }

        var inferred = Sanitize(InferFromUrl(url));
        if (!string.IsNullOrWhiteSpace(contentType)
            && string.IsNullOrEmpty(Path.GetExtension(inferred))
            && FileMime.ExtensionFor(contentType) is { } extension)
        {
            inferred += extension;
        }

        return inferred;
    }

    /// <summary>
    /// Trims a title to a short, filename-safe prefix (byte-capped, never
    /// splitting a character): the readable part of a storage folder name.
    /// </summary>
    public static string Shorten(string name, int maxBytes)
    {
        var cleaned = Sanitize(name);
        while (cleaned.Length > 1 && Encoding.UTF8.GetByteCount(cleaned) > maxBytes)
        {
            cleaned = cleaned[..^1].TrimEnd();
        }

        return cleaned;
    }

    /// <summary>Appends a task id before the extension: "name [id].ext" -
    /// the group-layout marker for files that clashed on the same name.</summary>
    public static string SuffixWithId(string name, string taskId)
    {
        var extension = Path.GetExtension(name);
        return $"{name[..^extension.Length]} [{taskId}]{extension}";
    }
}
