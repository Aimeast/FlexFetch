namespace FlexFetch.Services.Session;

/// <summary>
/// YouTube client-posture builders. Only measured postures are used:
/// with cookies mweb is the only reliable client; anonymous runs use
/// android_vr + mweb. android/tv MUST NOT be combined with cookies - yt-dlp
/// skips those clients outright and the leftover posture triggers the bot
/// wall. visitor_data pairs the session with its visitor identity.
/// </summary>
public static class YouTubePosture
{
    public const string CookieClients = "mweb";
    public const string AnonymousClients = "android_vr,mweb";

    public const string ExtractorArgsPrefix = "youtube:";

    /// <summary>Builds the extractor-args value for a client set (+ visitor
    /// identity when a session jar is in play).</summary>
    public static string BuildExtractorArgs(string clients, string? visitorData = null)
    {
        var args = $"{ExtractorArgsPrefix}player_client={clients}";
        return string.IsNullOrWhiteSpace(visitorData) ? args : $"{args};visitor_data={visitorData}";
    }
}
