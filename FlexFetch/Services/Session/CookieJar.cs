using FlexFetch.Entities;

namespace FlexFetch.Services.Session;

/// <summary>
/// Shared cookie-jar semantics for every site store: identity is the
/// (name, domain) pair (leading dot and case ignored), expired entries never
/// survive, and an incoming jar overwrites existing values in place.
/// </summary>
public static class CookieJar
{
    public static IReadOnlyList<CookieItem> Merge(
        IReadOnlyList<CookieItem> existing,
        IReadOnlyList<CookieItem> incoming)
    {
        var map = new Dictionary<(string Name, string Domain), CookieItem>();
        foreach (var cookie in existing)
        {
            if (!IsExpired(cookie))
            {
                map[Key(cookie)] = cookie;
            }
        }

        foreach (var cookie in incoming)
        {
            if (IsExpired(cookie))
            {
                map.Remove(Key(cookie));
                continue;
            }

            map[Key(cookie)] = cookie;
        }

        return map.Values.ToList();
    }

    private static (string, string) Key(CookieItem cookie) =>
        (cookie.Name, (cookie.Domain ?? string.Empty).TrimStart('.').ToLowerInvariant());

    private static bool IsExpired(CookieItem cookie) =>
        cookie.ExpiresAt is not null && cookie.ExpiresAt.Value.ToUniversalTime() <= DateTime.UtcNow;
}
