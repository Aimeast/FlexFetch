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
                    new TaskRepository(store), new ShareRepository(store), store, config,
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

    [TestMethod]
    public async Task Recovery_RunsBeforeSlowInstall()
    {
        // The regression this pins: a hung component install (a Firefox
        // download once stalled for the whole container lifetime behind a
        // broken proxy) must never leave the interrupted tasks invisible in
        // Running - recovery marks them Waiting up front, and the queue
        // opens only after the install finished.
        var steps = new List<string>();
        var installEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = StartupTasksHostedService.RunStartupSequenceAsync(
            TestLog.Instance,
            CancellationToken.None,
            recoverPending: () => { steps.Add("recover"); return 1; },
            reconcileFileNames: () => { steps.Add("reconcile"); return 0; },
            releaseWaitingTasks: () => { steps.Add("release"); return 0; },
            readyComponents: new[] { "yt-dlp", "deno", "ffmpeg" },
            missingComponents: new[] { "firefox" },
            autoInstallEnabled: true,
            installMissingComponents: async () =>
            {
                steps.Add("install-start");
                installEntered.TrySetResult();
                await release.Task;
                steps.Add("install-done");
            },
            signalComponentPlanLogged: () => steps.Add("signal"));

        await installEntered.Task;
        Assert.AreEqual("recover", steps[0], "recovery must run while the install is still going");
        Assert.IsTrue(steps.IndexOf("recover") < steps.IndexOf("install-start"));
        CollectionAssert.DoesNotContain(steps, "release");

        release.TrySetResult();
        await run;
        CollectionAssert.AreEqual(
            new[] { "recover", "reconcile", "install-start", "install-done", "release" },
            steps.Where(s => s is "recover" or "reconcile" or "install-start" or "install-done" or "release").ToList());
    }

    [TestMethod]
    public async Task Recovery_RunsEvenWhenAutoInstallDisabled()
    {
        var steps = new List<string>();
        var installRan = false;

        await StartupTasksHostedService.RunStartupSequenceAsync(
            TestLog.Instance,
            CancellationToken.None,
            recoverPending: () => { steps.Add("recover"); return 1; },
            reconcileFileNames: () => { steps.Add("reconcile"); return 0; },
            releaseWaitingTasks: () => { steps.Add("release"); return 0; },
            readyComponents: new[] { "yt-dlp", "deno", "ffmpeg" },
            missingComponents: new[] { "firefox" },
            autoInstallEnabled: false,
            installMissingComponents: () =>
            {
                installRan = true;
                return Task.CompletedTask;
            },
            signalComponentPlanLogged: () => steps.Add("signal"));

        Assert.IsTrue(steps.Contains("recover"), "a disabled install must not skip recovery");
        Assert.IsFalse(installRan);
        Assert.IsTrue(steps.Contains("release"), "held tasks would strand forever with no install coming");
        Assert.IsTrue(steps.Contains("signal"), "waiters on the component plan must be released");
    }

    [TestMethod]
    public async Task InstallFailure_DoesNotEscape_AndReleasesWaitingTasks()
    {
        var steps = new List<string>();

        await StartupTasksHostedService.RunStartupSequenceAsync(
            TestLog.Instance,
            CancellationToken.None,
            recoverPending: () => { steps.Add("recover"); return 1; },
            reconcileFileNames: () => { steps.Add("reconcile"); return 0; },
            releaseWaitingTasks: () => { steps.Add("release"); return 0; },
            readyComponents: Array.Empty<string>(),
            missingComponents: new[] { "yt-dlp" },
            autoInstallEnabled: true,
            installMissingComponents: () =>
            {
                steps.Add("install");
                throw new InvalidOperationException("boom");
            },
            signalComponentPlanLogged: () => steps.Add("signal"));

        Assert.AreEqual("recover", steps[0]);
        Assert.IsTrue(steps.Contains("install"));
        Assert.IsTrue(steps.Contains("release"), "a failed install must not strand the held tasks");
    }

    [TestMethod]
    public async Task InstallCancelled_TasksStayWaiting()
    {
        // A shutdown during the install leaves the held tasks in Waiting;
        // the next boot's recovery picks them up again.
        var steps = new List<string>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await StartupTasksHostedService.RunStartupSequenceAsync(
            TestLog.Instance,
            cts.Token,
            recoverPending: () => { steps.Add("recover"); return 1; },
            reconcileFileNames: () => { steps.Add("reconcile"); return 0; },
            releaseWaitingTasks: () => { steps.Add("release"); return 0; },
            readyComponents: Array.Empty<string>(),
            missingComponents: new[] { "yt-dlp" },
            autoInstallEnabled: true,
            installMissingComponents: () => throw new OperationCanceledException(cts.Token),
            signalComponentPlanLogged: () => steps.Add("signal"));

        Assert.AreEqual("recover", steps[0]);
        CollectionAssert.DoesNotContain(steps, "release");
    }

    [TestMethod]
    public async Task AllComponentsReady_ReleasesImmediately()
    {
        var steps = new List<string>();

        await StartupTasksHostedService.RunStartupSequenceAsync(
            TestLog.Instance,
            CancellationToken.None,
            recoverPending: () => { steps.Add("recover"); return 1; },
            reconcileFileNames: () => { steps.Add("reconcile"); return 0; },
            releaseWaitingTasks: () => { steps.Add("release"); return 0; },
            readyComponents: new[] { "yt-dlp", "deno", "ffmpeg", "firefox" },
            missingComponents: Array.Empty<string>(),
            autoInstallEnabled: true,
            installMissingComponents: () =>
            {
                steps.Add("install");
                return Task.CompletedTask;
            },
            signalComponentPlanLogged: () => steps.Add("signal"));

        Assert.AreEqual("recover", steps[0]);
        Assert.IsFalse(steps.Contains("install"));
        Assert.IsTrue(steps.Contains("release"));
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
