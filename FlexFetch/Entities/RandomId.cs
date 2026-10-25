using System.Security.Cryptography;

namespace FlexFetch.Entities;

/// <summary>
/// Generates random IDs. Task and record IDs use 48 bits (8 URL-safe base64
/// characters); secrets that grant access (share tokens, guest session ids)
/// use 128 bits (22 characters) so online guessing is infeasible.
/// </summary>
public static class RandomId
{
    /// <summary>
    /// Generates a new record id (48-bit entropy, 8 URL-safe base64 characters).
    /// </summary>
    public static string New()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return Encode(bytes);
    }

    /// <summary>
    /// Generates a new access secret (128-bit entropy, 22 URL-safe base64
    /// characters). Use this for anything that is itself a credential.
    /// </summary>
    public static string NewSecret()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Encode(bytes);
    }

    private static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
