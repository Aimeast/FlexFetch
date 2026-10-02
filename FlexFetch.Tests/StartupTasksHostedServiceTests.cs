using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.HostedServices;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using FlexFetch.Services.Session;
using FlexFetch.Services.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Tests;

[TestClass]
public sealed class StartupTasksHostedServiceTests
{
    [TestMethod]
    public async Task ComponentPlan_Gate_HoldsFirstRoundsUntilPlanLogged()
    {
        // The pot supervisor's first round can install components - it must
        // wait until the startup plan line is out, or "Installing yt-dlp"
        // would appear BEFORE the "Installing missing components" plan (the
        // boot-log ordering incident).
        var dir = TestApp.CreateTempDataDir();
        try
        {
            var store = new LiteDbStore(Path.Combine(dir, "flexfetch.db"));
            var config = new ConfigurationBuilder().Build();
            ILogger log = TestLog.Instance;
            var proxy = new NoProxyStub();
            var lifetime = new FireableLifetime();
            var storage = new StorageService(dir);

            var startup = new StartupTasksHostedService(
                lifetime,
                config,
                new UserService(new UserRepository(store), config),
                config,
                new FakeEnvironment(),
                new TaskService(
                    new TaskRepository(store), new ShareRepository(store), config,
                    new NoopExecutor(), storage, log),
                new YtdlpService(proxy, log, dir),
                new FirefoxBrowserService(proxy, storage, log),
                log);
            var pot = new PotProviderService(new YtdlpService(proxy, log, dir), proxy, log);
            var supervisor = new PotSupervisorHostedService(pot, startup, lifetime, log);

            // While the gate is closed, a first-round install trigger waits.
            var roundCts = new CancellationTokenSource();
            var firstRound = supervisor.ExecuteOnceForTestAsync(roundCts.Token);
            await Assert.ThrowsExactlyAsync<TimeoutException>(
                () => firstRound.WaitAsync(TimeSpan.FromMilliseconds(300)));
            Assert.IsFalse(firstRound.IsCompleted, "the round must still be waiting on the gate");

            // Cancel the round while it is still gated, so no pot work runs.
            roundCts.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => firstRound);

            // Starting the service opens the gate: the plan is logged before
            // any install begins. The empty config disables auto-install, so
            // this path exercises the plan without touching the network.
            await startup.StartAsync(CancellationToken.None);
            lifetime.FireStarted();
            await startup.WhenComponentPlanLogged.WaitAsync(TimeSpan.FromSeconds(5));

            await startup.StopAsync(CancellationToken.None);
            store.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class FireableLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }

        public void FireStarted() => _started.Cancel();
    }

    private sealed class FakeEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "FlexFetch.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = string.Empty;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class NoopExecutor : ITaskExecutor
    {
        public Task<TaskExecutionResult> ExecuteAsync(TaskItem task, Action<double> progress, CancellationToken cancellationToken)
        {
            progress(1.0);
            return Task.FromResult(TaskExecutionResult.Completed);
        }
    }

    private sealed class NoProxyStub : IProxyService
    {
        public bool ShouldProxy(Uri url) => false;

        public bool ShouldProxyFast(Uri url) => false;

        public HttpMessageHandler CreateHandler(Uri url) => new SocketsHttpHandler { UseProxy = false };

        public string? GetProxyUri(Uri url) => null;
    }
}
