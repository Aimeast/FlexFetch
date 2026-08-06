using System.Text;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// File naming rules: strip illegal characters, cap length (UTF-8 aware),
/// infer names from URLs, and keep names unique within a task.
/// </summary>
public static class FileNameRules
{
    private const int MaxNameBytes = 180;

    private static readonly char[] IllegalChars = Path.GetInvalidFileNameChars();

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
}
