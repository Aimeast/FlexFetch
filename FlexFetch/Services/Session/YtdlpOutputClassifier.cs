using FlexFetch.Services.Downloaders;

namespace FlexFetch.Services.Session;

/// <summary>
/// Classification of one yt-dlp run's output, per the session playbook's
/// output table. The class decides the disposition: retry with a session,
/// trigger a re-export, change the client posture, or treat as a plain
/// business failure.
/// </summary>
public enum YtdlpOutputClass
{
    /// <summary>The jar's cookies are mutually inconsistent (e.g. a mixed
    /// jar); only a fresh import can fix it - a re-export cannot.</summary>
    JarInconsistent,

    /// <summary>Session cookies were rotated/invalidated server-side. Fatal
    /// even when the exit code is 0 (yt-dlp falls back to anonymous).</summary>
    SessionRotated,

    /// <summary>Bot challenge ("Sign in to confirm you're not a bot").</summary>
    BotCheck,

    /// <summary>Client rejected ("The page needs to be reloaded") - change
    /// the player-client posture.</summary>
    ClientBlocked,

    /// <summary>A signed-in session is required.</summary>
    LoginRequired,

    /// <summary>The resource is age-restricted.</summary>
    AgeRestricted,

    /// <summary>The resource is private.</summary>
    Private,

    /// <summary>The resource is members-only.</summary>
    MembersOnly,

    /// <summary>The content is gone/unavailable - NOT an auth problem, must
    /// never trigger a session re-export.</summary>
    DeadContent,

    /// <summary>Transient failure (timeout, 429, 5xx, proxy fault).</summary>
    Retryable,

    /// <summary>No known marker matched and the run reported no failure.</summary>
    Ok,

    /// <summary>No known marker matched.</summary>
    Unknown,
}

/// <summary>
/// Classifies yt-dlp output lines (stderr lines, both WARNING and ERROR)
/// into a single verdict. Every line is classified first and the most
/// significant verdict wins - a run must never be judged by its exit code
/// alone, because a rotated jar still "succeeds" via anonymous fallback
/// with only a WARNING.
/// </summary>
public static class YtdlpOutputClassifier
{
    /// <summary>Lower = more significant; the minimum class wins.</summary>
    private static int Priority(YtdlpOutputClass c) => c switch
    {
        YtdlpOutputClass.JarInconsistent => 0,
        YtdlpOutputClass.SessionRotated => 1,
        YtdlpOutputClass.BotCheck => 2,
        YtdlpOutputClass.ClientBlocked => 3,
        YtdlpOutputClass.LoginRequired => 4,
        YtdlpOutputClass.AgeRestricted => 5,
        YtdlpOutputClass.Private => 6,
        YtdlpOutputClass.MembersOnly => 7,
        YtdlpOutputClass.DeadContent => 8,
        YtdlpOutputClass.Retryable => 9,
        YtdlpOutputClass.Ok => 10,
        _ => 11,
    };

    /// <summary>
    /// True for the classes where attaching a valid session could still help
    /// (login/bot/age/private/members) - the retry-with-cookies trigger.
    /// </summary>
    public static bool IsAuthFamily(YtdlpOutputClass c) => c is
        YtdlpOutputClass.BotCheck or
        YtdlpOutputClass.LoginRequired or
        YtdlpOutputClass.AgeRestricted or
        YtdlpOutputClass.Private or
        YtdlpOutputClass.MembersOnly;

    /// <summary>Maps an auth-family class onto the task-facing failure reason.</summary>
    public static AuthFailureReason ToAuthFailureReason(YtdlpOutputClass c) => c switch
    {
        YtdlpOutputClass.BotCheck => AuthFailureReason.LoginRequired,
        YtdlpOutputClass.LoginRequired => AuthFailureReason.LoginRequired,
        YtdlpOutputClass.AgeRestricted => AuthFailureReason.AgeRestricted,
        YtdlpOutputClass.Private => AuthFailureReason.Private,
        YtdlpOutputClass.MembersOnly => AuthFailureReason.MembersOnly,
        _ => AuthFailureReason.Unknown,
    };

    /// <summary>Classifies the complete output of one yt-dlp run.</summary>
    public static YtdlpOutputClass Classify(IReadOnlyList<string> outputLines, bool runSucceeded)
    {
        var best = runSucceeded ? YtdlpOutputClass.Ok : YtdlpOutputClass.Unknown;
        foreach (var line in outputLines)
        {
            var c = ClassifyLine(line);
            if (c == YtdlpOutputClass.Unknown)
            {
                continue;
            }

            if (Priority(c) < Priority(best))
            {
                best = c;
            }
        }

        return best;
    }

    /// <summary>Classifies a single output line (WARNING or ERROR, any case).</summary>
    public static YtdlpOutputClass ClassifyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return YtdlpOutputClass.Unknown;
        }

        // CookieMismatch means the jar's cookies contradict each other; an
        // accounts.google.com rejection of the presented jar is the same
        // family. Only a fresh import can fix these.
        if (line.Contains("cookiemismatch", StringComparison.OrdinalIgnoreCase)
            || line.Contains("cookie mismatch", StringComparison.OrdinalIgnoreCase)
            || line.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return YtdlpOutputClass.JarInconsistent;
        }

        // "The cookies are no longer valid" / rotated session warnings are
        // fatal even as WARNING (exit code 0 still counts as failure).
        if (line.Contains("no longer valid", StringComparison.OrdinalIgnoreCase)
            || line.Contains(" cookies have been rotated", StringComparison.OrdinalIgnoreCase)
            || line.Contains("cookies are no longer", StringComparison.OrdinalIgnoreCase))
        {
            return YtdlpOutputClass.SessionRotated;
        }

        // Bot challenge. The apostrophe inside "you're" has an unstable
        // encoding across environments, so only stable substrings are used.
        if (line.Contains("sign in to confirm", StringComparison.OrdinalIgnoreCase)
            || line.Contains("not a bot", StringComparison.OrdinalIgnoreCase))
        {
            return YtdlpOutputClass.BotCheck;
        }

        // Client blocked: the player client was rejected; changing the
        // posture (player_client set) is the answer.
        if (line.Contains("page needs to be reloaded", StringComparison.OrdinalIgnoreCase)
            || line.Contains("please reload the page", StringComparison.OrdinalIgnoreCase))
        {
            return YtdlpOutputClass.ClientBlocked;
        }

        if (line.Contains("login_required", StringComparison.OrdinalIgnoreCase)
            || line.Contains("login required", StringComparison.OrdinalIgnoreCase)
            || line.Contains("please sign in", StringComparison.OrdinalIgnoreCase))
        {
            return YtdlpOutputClass.LoginRequired;
        }

        if (line.Contains("age-restricted", StringComparison.OrdinalIgnoreCase)
            || line.Contains("age restricted", StringComparison.OrdinalIgnoreCase))
        {
            return YtdlpOutputClass.AgeRestricted;
        }

        if (line.Contains("private video", StringComparison.OrdinalIgnoreCase))
        {
            return YtdlpOutputClass.Private;
        }

        if (line.Contains("members only", StringComparison.OrdinalIgnoreCase)
            || line.Contains("members-only", StringComparison.OrdinalIgnoreCase)
            || line.Contains("member-only", StringComparison.OrdinalIgnoreCase))
        {
            return YtdlpOutputClass.MembersOnly;
        }

        // Dead content is a business failure, never an auth problem: it must
        // not trigger a session re-export.
        if (line.Contains("video unavailable", StringComparison.OrdinalIgnoreCase)
            || line.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || line.Contains("has been removed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("no longer available", StringComparison.OrdinalIgnoreCase))
        {
            return YtdlpOutputClass.DeadContent;
        }

        // Transient failures: timeouts, rate limiting, server errors, and the
        // googlevideo (GVS) CDN 403 jitter.
        var isGvs403 = line.Contains("googlevideo", StringComparison.OrdinalIgnoreCase)
            && line.Contains("http error 403", StringComparison.OrdinalIgnoreCase);
        if (isGvs403
            || line.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || line.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unable to connect", StringComparison.OrdinalIgnoreCase)
            || line.Contains("connection error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed to establish", StringComparison.OrdinalIgnoreCase)
            || line.Contains("could not connect", StringComparison.OrdinalIgnoreCase)
            || line.Contains("socks", StringComparison.OrdinalIgnoreCase)
            || line.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
            || line.Contains("http error 429", StringComparison.OrdinalIgnoreCase)
            || line.Contains("http error 5", StringComparison.OrdinalIgnoreCase))
        {
            return YtdlpOutputClass.Retryable;
        }

        return YtdlpOutputClass.Unknown;
    }
}
