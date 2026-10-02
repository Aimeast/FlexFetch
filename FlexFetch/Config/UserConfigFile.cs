using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace FlexFetch.Config;

/// <summary>
/// The editable runtime configuration on the data directory: seeded from
/// the template bundled with the app on first start, never overwritten
/// again - recreating the container keeps user edits. Program.cs loads it
/// last with reloadOnChange, so volume-persistent edits override
/// appsettings*.json without touching the image (where the consuming code
/// re-reads configuration, e.g. the network proxy policy). The template
/// also owns the Kestrel endpoints: http 5080 + https 5081 with the
/// certificate files from the /certs mount.
/// </summary>
public static class UserConfigFile
{
    /// <summary>The editable configuration inside the data directory.</summary>
    public const string FileName = "appsettings.json";

    /// <summary>The bundled template (image builds only; a dev run has none
    /// and seeds nothing). The name deliberately does NOT match the host's
    /// appsettings.{Environment}.json scheme: the template must never become
    /// a configuration source itself, or its Kestrel section would survive
    /// in the merged configuration even after
    /// <see cref="RemoveUnusableHttpsEndpoints"/> dropped it from the user
    /// file (sources merge per key).</summary>
    public const string TemplateFileName = "appsettings.template.json";

    /// <summary>Http endpoint injected when the configuration carries no
    /// Kestrel:Endpoints section (a hand-written minimal file must not
    /// leave the container without a listener).</summary>
    public const string DefaultHttpUrl = "http://*:5080";

    public static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

    public static string TemplatePath(string baseDirectory) =>
        Path.Combine(baseDirectory, TemplateFileName);

    /// <summary>Seeds the data-dir configuration from the bundled template.
    /// No-op when the template is absent (development) or the user file
    /// already exists (an existing file is never overwritten).</summary>
    public static void Seed(string dataDir, string baseDirectory)
    {
        var template = TemplatePath(baseDirectory);
        var target = PathFor(dataDir);
        if (!File.Exists(template) || File.Exists(target))
        {
            return;
        }

        Directory.CreateDirectory(dataDir);
        File.Copy(template, target);
    }

    /// <summary>
    /// True when the file parses as JSON (an absent file is simply "no user
    /// configuration", not an error). A malformed user edit must not brick
    /// startup: the caller moves the file aside instead of loading it.
    /// </summary>
    public static bool TryValidate(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return true;
        }
        catch (JsonException ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Injects the default http endpoint into a configuration file without
    /// a Kestrel:Endpoints section. A file already carrying endpoints is
    /// user-managed (https included) and left untouched; a malformed file
    /// is skipped (handled by <see cref="TryValidate"/>).
    /// </summary>
    public static void EnsureHttpEndpoint(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null || root["Kestrel"]?["Endpoints"] is not null)
            {
                return;
            }

            root["Kestrel"] = new JsonObject
            {
                ["Endpoints"] = new JsonObject
                {
                    ["Http"] = new JsonObject { ["Url"] = DefaultHttpUrl },
                },
            };
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException)
        {
            // Malformed edits are moved aside by the TryValidate caller.
        }
    }

    /// <summary>
    /// Rewrites the configuration file dropping https endpoints whose
    /// certificate cannot be used - an inline certificate pointing at
    /// missing files, or a certificate name reference (a schema Kestrel
    /// ignores, so binding would die with "No server certificate was
    /// specified"). The deployment gives up https and keeps serving http
    /// instead of failing as a whole; each removal is reported. Restoring
    /// the certificate files and re-adding the endpoint re-enables https on
    /// the next start.
    /// </summary>
    public static IReadOnlyList<string> RemoveUnusableHttpsEndpoints(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var endpoints = root?["Kestrel"]?["Endpoints"] as JsonObject;
            if (endpoints is null)
            {
                return [];
            }

            List<string>? notices = null;
            foreach (var name in endpoints.Select(e => e.Key).ToArray())
            {
                if (endpoints[name] is not JsonObject endpoint
                    || StringOrNull(endpoint["Url"]) is not { } url
                    || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var problem = UnusableCertificateReason(endpoint);
                if (problem is null)
                {
                    continue;
                }

                endpoints.Remove(name);
                (notices ??= []).Add(
                    $"https endpoint '{name}' ({url}) dropped: {problem} - giving up https, http keeps serving");
            }

            if (notices is null)
            {
                return [];
            }

            if (endpoints.Count == 0)
            {
                // Leave no empty section behind: the http-endpoint default
                // applied afterwards injects a fresh one.
                (root!["Kestrel"] as JsonObject)?.Remove("Endpoints");
            }

            File.WriteAllText(path, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return notices;
        }
        catch (JsonException)
        {
            // Malformed edits are moved aside by the TryValidate caller.
            return [];
        }
    }

    /// <summary>Why the endpoint's https certificate cannot be used, or null
    /// when it binds (or is of an unassessable form Kestrel may still
    /// resolve, e.g. a certificate store lookup).</summary>
    private static string? UnusableCertificateReason(JsonObject endpoint)
    {
        var certificate = endpoint["Certificate"];
        if (certificate is JsonValue)
        {
            return "certificate is referenced by name, which Kestrel does not support (inline Path/KeyPath only)";
        }

        if (certificate is not JsonObject cert)
        {
            // No inline certificate: Kestrel resolves Kestrel:Certificates
            // defaults or the development certificate - not assessable here.
            return null;
        }

        if (StringOrNull(cert["Name"]) is { } name && StringOrNull(cert["Path"]) is null)
        {
            return $"certificate '{name}' is referenced by name, which Kestrel does not support (inline Path/KeyPath only)";
        }

        var files = new[] { StringOrNull(cert["Path"]), StringOrNull(cert["KeyPath"]) }
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .OfType<string>()
            .ToArray();
        if (files.Length == 0)
        {
            // A password-protected PFX or a store lookup: not assessable here.
            return null;
        }

        var missing = files.Where(MissingFile).Select(f => $"'{f}' not found").ToList();
        return missing.Count > 0 ? string.Join(", ", missing) : null;
    }

    /// <summary>The string value of a JSON node, or null for absent or
    /// non-string nodes.</summary>
    private static string? StringOrNull(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>True when the file does not exist; relative paths resolve
    /// against the current working directory, mirroring how Kestrel loads
    /// them.</summary>
    private static bool MissingFile(string file) =>
        !File.Exists(Path.IsPathRooted(file) ? file : Path.Combine(Directory.GetCurrentDirectory(), file));
}
