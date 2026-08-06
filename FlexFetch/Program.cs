using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog: structured logging to console and rolling files.
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: "logs/flexfetch-.log",
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 100 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();

var app = builder.Build();

app.MapGet("/", () => "FlexFetch is running.");

app.Run();

// Expose the generated Program class for integration tests (WebApplicationFactory).
public partial class Program { }
