using FlexFetch.Config;
using Microsoft.Extensions.Configuration;

namespace FlexFetch.Tests;

[TestClass]
public sealed class UserConfigFileTests
{
    private string _dataDir = null!;
    private string _baseDir = null!;

    [TestInitialize]
    public void CreateTempDirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "ucfg-" + Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(root, "data");
        _baseDir = Path.Combine(root, "app");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_baseDir);
    }

    [TestCleanup]
    public void RemoveTempDirs()
    {
        Directory.Delete(Path.GetDirectoryName(_dataDir)!, recursive: true);
    }

    // --- Seed ---

    [TestMethod]
    public void Seed_WithTemplate_CopiesIntoDataDir()
    {
        File.WriteAllText(UserConfigFile.TemplatePath(_baseDir), """{ "Network": { "Proxy": "http://p:1" } }""");

        UserConfigFile.Seed(_dataDir, _baseDir);

        var seeded = File.ReadAllText(UserConfigFile.PathFor(_dataDir));
        Assert.Contains("http://p:1", seeded);
    }

    [TestMethod]
    public void Seed_ExistingUserFile_NeverOverwritten()
    {
        File.WriteAllText(UserConfigFile.TemplatePath(_baseDir), """{ "Network": { "Proxy": "template" } }""");
        File.WriteAllText(UserConfigFile.PathFor(_dataDir), """{ "Network": { "Proxy": "user-edit" } }""");

        UserConfigFile.Seed(_dataDir, _baseDir);

        Assert.Contains("user-edit", File.ReadAllText(UserConfigFile.PathFor(_dataDir)));
    }

    [TestMethod]
    public void Seed_WithoutTemplate_NoFileCreated()
    {
        // A dev run has no bundled template; seeding must stay a no-op.
        UserConfigFile.Seed(_dataDir, _baseDir);

        Assert.IsFalse(File.Exists(UserConfigFile.PathFor(_dataDir)));
    }

    // --- TryValidate ---

    [TestMethod]
    public void TryValidate_MissingFile_NotAnError()
    {
        Assert.IsFalse(UserConfigFile.TryValidate(
            Path.Combine(_dataDir, "absent.json"), out var problem));
        Assert.IsNull(problem);
    }

    [TestMethod]
    public void TryValidate_ValidJson_True()
    {
        var path = Path.Combine(_dataDir, UserConfigFile.FileName);
        File.WriteAllText(path, """{ "Network": { "Proxy": "" } }""");

        Assert.IsTrue(UserConfigFile.TryValidate(path, out var problem));
        Assert.IsNull(problem);
    }

    [TestMethod]
    public void TryValidate_MalformedJson_FalseWithProblem()
    {
        var path = Path.Combine(_dataDir, UserConfigFile.FileName);
        File.WriteAllText(path, """{ "Network": { "Proxy": "" }""");

        Assert.IsFalse(UserConfigFile.TryValidate(path, out var problem));
        Assert.IsNotNull(problem);
    }

    // --- EnsureHttpEndpoint ---

    [TestMethod]
    public void EnsureHttpEndpoint_NoKestrelSection_InjectsHttpOnly()
    {
        var path = UserConfigFile.PathFor(_dataDir);
        File.WriteAllText(path, """{ "Network": { "Proxy": "keep" } }""");

        UserConfigFile.EnsureHttpEndpoint(path);

        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.AreEqual(UserConfigFile.DefaultHttpUrl, node["Kestrel"]!["Endpoints"]!["Http"]!["Url"]!.GetValue<string>());
        Assert.AreEqual("keep", node["Network"]!["Proxy"]!.GetValue<string>());
    }

    [TestMethod]
    public void EnsureHttpEndpoint_KestrelManaged_LeftUntouched()
    {
        var path = UserConfigFile.PathFor(_dataDir);
        const string original = """{ "Kestrel": { "Endpoints": { "HttpsInlineCertFile": { "Url": "https://0.0.0.0:5443" } } } }""";
        File.WriteAllText(path, original);

        UserConfigFile.EnsureHttpEndpoint(path);

        Assert.AreEqual(original, File.ReadAllText(path));
    }

    [TestMethod]
    public void EnsureHttpEndpoint_MalformedOrMissing_NoChange()
    {
        var malformed = Path.Combine(_dataDir, UserConfigFile.FileName);
        File.WriteAllText(malformed, "{ broken");
        var absent = Path.Combine(_dataDir, "absent.json");

        UserConfigFile.EnsureHttpEndpoint(malformed);
        UserConfigFile.EnsureHttpEndpoint(absent);

        Assert.IsFalse(File.Exists(absent));
    }

    // --- RemoveUnusableHttpsEndpoints ---

    private (string Cert, string Key) CreateCertificateFiles()
    {
        var cert = Path.Combine(_dataDir, "server-cert.pem");
        var key = Path.Combine(_dataDir, "server-key.pem");
        File.WriteAllText(cert, "cert");
        File.WriteAllText(key, "key");
        return (cert, key);
    }

    private string WriteConfig(string json)
    {
        var path = UserConfigFile.PathFor(_dataDir);
        File.WriteAllText(path, json);
        return path;
    }

    [TestMethod]
    public void Remove_MissingFile_NoChange()
    {
        Assert.IsEmpty(UserConfigFile.RemoveUnusableHttpsEndpoints(
            Path.Combine(_dataDir, "absent.json")));
    }

    [TestMethod]
    public void Remove_HttpEndpoints_Kept()
    {
        var path = WriteConfig("""{ "Kestrel": { "Endpoints": { "Http": { "Url": "http://*:5080" } } } }""");

        Assert.IsEmpty(UserConfigFile.RemoveUnusableHttpsEndpoints(path));
        Assert.Contains("http://*:5080", File.ReadAllText(path));
    }

    [TestMethod]
    public void Remove_CertFilesMissing_EndpointDropped_HttpKept()
    {
        var path = WriteConfig("""
            {
              "Kestrel": {
                "Endpoints": {
                  "Http": { "Url": "http://*:5080" },
                  "Https": {
                    "Url": "https://*:5081",
                    "Certificate": { "Path": "/certs/cert.pem", "KeyPath": "/certs/privkey.pem" }
                  }
                }
              }
            }
            """);

        var notices = UserConfigFile.RemoveUnusableHttpsEndpoints(path);

        Assert.HasCount(1, notices);
        Assert.Contains("/certs/cert.pem", notices[0]);
        Assert.Contains("giving up https", notices[0]);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.IsNull(node["Kestrel"]!["Endpoints"]!["Https"]);
        Assert.AreEqual("http://*:5080", node["Kestrel"]!["Endpoints"]!["Http"]!["Url"]!.GetValue<string>());
    }

    [TestMethod]
    public void Remove_CertFilesPresent_Kept()
    {
        var (cert, key) = CreateCertificateFiles();
        var path = WriteConfig($$"""
            {
              "Kestrel": {
                "Endpoints": {
                  "Https": {
                    "Url": "https://*:5081",
                    "Certificate": { "Path": "{{cert.Replace("\\", "\\\\")}}", "KeyPath": "{{key.Replace("\\", "\\\\")}}" }
                  }
                }
              }
            }
            """);

        Assert.IsEmpty(UserConfigFile.RemoveUnusableHttpsEndpoints(path));
        Assert.Contains("https://*:5081", File.ReadAllText(path));
    }

    [TestMethod]
    public void Remove_NameReference_Dropped()
    {
        // A certificate referenced by name is not a supported endpoint
        // schema: Kestrel ignores it and binding fails with "No server
        // certificate was specified" - the endpoint is dropped instead.
        CreateCertificateFiles();
        var path = WriteConfig("""
            {
              "Kestrel": {
                "Endpoints": {
                  "Https": { "Url": "https://*:5081", "Certificate": "FlexFetchPem" }
                }
              }
            }
            """);

        var notices = UserConfigFile.RemoveUnusableHttpsEndpoints(path);

        Assert.HasCount(1, notices);
        Assert.Contains("referenced by name", notices[0]);
        Assert.IsNull(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["Kestrel"]?["Endpoints"]?["Https"]);
    }

    [TestMethod]
    public void Remove_NameOnlyObjectForm_Dropped()
    {
        var path = WriteConfig("""
            {
              "Kestrel": {
                "Endpoints": {
                  "Https": { "Url": "https://*:5081", "Certificate": { "Name": "FlexFetchPem" } }
                }
              }
            }
            """);

        var notices = UserConfigFile.RemoveUnusableHttpsEndpoints(path);

        Assert.HasCount(1, notices);
        Assert.Contains("referenced by name", notices[0]);
    }

    [TestMethod]
    public void Remove_CertificateStoreLookup_LeftAlone()
    {
        var path = WriteConfig("""
            {
              "Kestrel": {
                "Endpoints": {
                  "Https": {
                    "Url": "https://*:5081",
                    "Certificate": { "Subject": "FlexFetch", "Store": "My" }
                  }
                }
              }
            }
            """);

        Assert.IsEmpty(UserConfigFile.RemoveUnusableHttpsEndpoints(path));
        Assert.Contains("https://*:5081", File.ReadAllText(path));
    }

    [TestMethod]
    public void Remove_LastEndpoint_KestrelSectionCleared_ThenHttpInjected()
    {
        var path = WriteConfig("""
            {
              "Kestrel": {
                "Endpoints": {
                  "Https": {
                    "Url": "https://*:5081",
                    "Certificate": { "Path": "/certs/cert.pem", "KeyPath": "/certs/privkey.pem" }
                  }
                }
              }
            }
            """);

        var notices = UserConfigFile.RemoveUnusableHttpsEndpoints(path);

        Assert.HasCount(1, notices);
        // No endpoints survive: the Kestrel section is cleared so the
        // http-endpoint default applies and the container keeps a listener.
        Assert.DoesNotContain("Https", File.ReadAllText(path));
        UserConfigFile.EnsureHttpEndpoint(path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.AreEqual(UserConfigFile.DefaultHttpUrl, node["Kestrel"]!["Endpoints"]!["Http"]!["Url"]!.GetValue<string>());
    }
}
