using Microsoft.AspNetCore.Mvc.Testing;

namespace FlexFetch.Tests;

/// <summary>
/// Creates the application against an isolated temporary data directory
/// so tests never touch the repository's real data folder.
/// </summary>
public static class TestApp
{
    public static string CreateTempDataDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flexfetch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static WebApplicationFactory<Program> CreateFactory(string dataDir, string adminPassword = "admin-pass-1")
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Data:Dir", dataDir);
            builder.UseSetting("Admin:InitialPassword", adminPassword);
            // Disable file logging so the temporary data dir is not locked
            // by a rolling log file during cleanup.
            builder.UseSetting("Logging:WriteToFile", "false");
        });
    }
}
