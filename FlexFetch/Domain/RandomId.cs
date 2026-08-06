using System.Security.Cryptography;

namespace FlexFetch.Domain;

/// <summary>
/// Generates high-entropy random IDs (48-bit, URL-safe base64, 8 characters).
/// IDs are unguessable and not derived from sequential numbers.
/// </summary>
public static class RandomId
{
    /// <summary>
    /// Generates a new random ID (48-bit entropy, 8 URL-safe base64 characters).
    /// </summary>
    public static string New()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
