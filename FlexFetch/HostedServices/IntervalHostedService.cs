using ILogger = Serilog.ILogger;

namespace FlexFetch.HostedServices;

/// <summary>
/// Base class for periodic background services: runs one iteration, waits
/// the configured interval, and repeats until shutdown. Iteration failures
/// are logged and do not stop the loop.
/// </summary>
public abstract class IntervalHostedService : BackgroundService
{
    private readonly ILogger _log;
    private readonly string _name;
    private readonly IHostApplicationLifetime _lifetime;

    protected IntervalHostedService(IHostApplicationLifetime lifetime, ILogger log, string name)
    {
        _lifetime = lifetime;
        _log = log;
        _name = name;
    }

    /// <summary>Interval between iterations, read fresh each cycle.</summary>
    protected abstract TimeSpan GetInterval();

    /// <summary>One work iteration.</summary>
    protected abstract Task ExecuteOnceAsync(CancellationToken cancellationToken);

    /// <summary>Runs a single iteration directly (used by unit tests).</summary>
    public Task ExecuteOnceForTestAsync(CancellationToken cancellationToken) =>
        ExecuteOnceAsync(cancellationToken);

    /// <summary>When false, the first iteration waits a full interval before running.</summary>
    protected virtual bool RunImmediately => true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield before any work so StartAsync returns immediately and the
        // host (Kestrel) can start listening without waiting on this service.
        await Task.Yield();

        // Do not start periodic work until the main service has finished
        // starting (Kestrel is listening), so background jobs never compete
        // with startup for resources.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        try
        {
            await started.Task.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return; // shutting down before startup finished
        }

        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = GetInterval();
            if (interval <= TimeSpan.Zero)
            {
                interval = TimeSpan.FromHours(1);
            }

            if (first && !RunImmediately)
            {
                first = false;
                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            try
            {
                await ExecuteOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "{Name} iteration failed", _name);
            }

            first = false;
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
