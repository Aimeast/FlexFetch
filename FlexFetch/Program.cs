using System.Text;
using FlexFetch.Startup;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// Child-process entry used by the startup service to install the headless
// browser: runs Playwright's install commands and exits without starting
// the web application. Must be checked before any host setup and never
// returns.
if (args.Length > 0 && args[0] == "--install-browser")
{
    BrowserInstallChild.Run(args);
}

// OS-operator command: reset an account password from the server shell and
// exit without starting the web application.
if (args.Length > 0 && args[0] == "--reset-password")
{
    return PasswordResetChild.Run(args);
}

var builder = WebApplication.CreateBuilder(args);
var dataDir = builder.AddFlexFetchRuntime();
builder.AddFlexFetchLogging(dataDir);
builder.Services.AddFlexFetchServices(dataDir, builder.Configuration);

var app = builder.Build();
app.UseFlexFetchPipeline();
app.Run();
return 0;

// Expose the generated Program class for integration tests (WebApplicationFactory).
public partial class Program
{
    /// <summary>Process start time (UTC), surfaced by the system info API.</summary>
    public static readonly DateTime StartedAt = DateTime.UtcNow;

    /// <summary>True when shutdown was requested via the system API, so the
    /// stop log can distinguish it from Ctrl+C / host signals.</summary>
    public static volatile bool ShutdownByApi;

    /// <summary>
    /// Resolves the data directory; implementation lives in RuntimeSetup.
    /// Kept here because tests and the browser-install child entry point
    /// reach it through the Program class.
    /// </summary>
    public static string ResolveDataDir(IConfiguration configuration)
        => RuntimeSetup.ResolveDataDir(configuration);
}
