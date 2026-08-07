namespace FlexFetch.Services;

/// <summary>
/// Maps file extensions to MIME types for media (audio/video) so the
/// browser can play them inline. Every other file type is served as
/// application/octet-stream (forced download). Sources:
/// chromium mime_util.cc (kPrimaryMappings/kSecondaryMappings) and the
/// Android mime.types list; only the audio and video entries are kept.
/// </summary>
public static class FileMime
{
    // Ordered (mime, comma-separated extensions) list; the first mapping of
    // an extension wins, so standard types (video/mp4, video/webm, audio/mpeg)
    // take precedence over aliases.
    private static readonly (string Mime, string Extensions)[] MediaMappings =
    {
        // Video
        ("video/webm", "webm"),
        ("video/mp4", "mp4,m4v"),
        ("video/ogg", "ogv,ogm"),
        ("video/mpeg", "mpeg,mpg,mpe"),
        ("video/3gpp", "3gp"),
        ("video/annodex", "axv"),
        ("video/avi", "avi"),
        ("video/divx", "divx"),
        ("video/dl", "dl"),
        ("video/dv", "dif,dv"),
        ("video/flc", "flc,fli"),
        ("video/fli", "fli"),
        ("video/gl", "gl"),
        ("video/mp2t", "ts"),
        ("video/quicktime", "mov,qt"),
        ("video/sd-video", "sdv"),
        ("video/vnd.mpegurl", "mxu"),
        ("video/x-dv", "dv,dif"),
        ("video/x-flv", "flv"),
        ("video/x-la-asf", "lsf,lsx"),
        ("video/x-m4v", "m4v"),
        ("video/x-matroska", "mpv,mkv"),
        ("video/x-mng", "mng"),
        ("video/x-ms-asf", "asf,asx"),
        ("video/x-ms-wm", "wm"),
        ("video/x-ms-wmv", "wmv"),
        ("video/x-ms-wmx", "wmx"),
        ("video/x-ms-wvx", "wvx"),
        ("video/x-msvideo", "avi"),
        ("video/x-mpeg", "mpg"),
        ("video/x-sgi-movie", "movie"),
        // Audio — standard types first so m4a maps to audio/mp4 and wav to
        // audio/wav instead of their aliases.
        ("audio/mpeg", "mp3,mpga,mpega,mp2"),
        ("audio/mp3", "mp3"),
        ("audio/mpeg3", "mp3"),
        ("audio/mp4", "m4a,mp4"),
        ("audio/x-m4a", "m4a"),
        ("audio/wav", "wav"),
        ("audio/x-wav", "wav"),
        ("audio/vnd.wave", "wav"),
        ("audio/aac", "aac"),
        ("audio/aiff", "aiff,aif,aifc"),
        ("audio/x-aiff", "aiff,aif,aifc"),
        ("audio/amr", "amr"),
        ("audio/amr-wb", "awb"),
        ("audio/annodex", "axa"),
        ("audio/basic", "au,snd"),
        ("audio/csound", "csd,orc,sco"),
        ("audio/flac", "flac"),
        ("audio/midi", "midi,mid,kar"),
        ("audio/mpegurl", "m3u"),
        ("audio/x-mpegurl", "m3u"),
        ("audio/ogg", "ogg,oga,opus,spx"),
        ("audio/vorbis", "ogg"),
        ("audio/prs.sid", "sid"),
        ("audio/vnd.rn-realaudio", "ra,ram"),
        ("audio/webm", "webm"),
        ("audio/x-gsm", "gsm"),
        ("audio/x-ms-wax", "wax"),
        ("audio/x-ms-wma", "wma"),
        ("audio/x-pn-realaudio", "ra,rm,ram"),
        ("audio/x-realaudio", "ra"),
        ("audio/x-scpls", "pls"),
        ("audio/x-sd2", "sd2"),
    };

    private static readonly Dictionary<string, string> ByExtension = BuildMap();

    private static Dictionary<string, string> BuildMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mime, extensions) in MediaMappings)
        {
            foreach (var ext in extensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                map.TryAdd("." + ext, mime);
            }
        }

        return map;
    }

    /// <summary>Returns the MIME type for a media file name, or octet-stream.</summary>
    public static string For(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return "application/octet-stream";
        }

        var ext = Path.GetExtension(fileName);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";
    }
}
