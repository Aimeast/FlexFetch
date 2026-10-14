namespace FlexFetch.Services.Session;

/// <summary>
/// Serialized runner for one site's session pipeline: the single-flight lock,
/// the "already running" rejection, stage/detail tracking for the status API
/// and last-run bookkeeping. Site-agnostic by design - the health vocabulary
/// and the outcome interpretation stay with the caller (the pipeline only
/// records the wall time and the error text).
/// </summary>
public sealed class SessionPipeline
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _busyMessage;

    /// <param name="busyMessage">Exception text used when a second run is
    /// attempted while the lock is held.</param>
    public SessionPipeline(string busyMessage)
    {
        _busyMessage = busyMessage;
    }

    /// <summary>True while a pipeline attempt holds the lock.</summary>
    public bool IsRunning => _lock.CurrentCount == 0;

    /// <summary>Pipeline stage currently executing (for the status API).</summary>
    public string CurrentStage { get; private set; } = "idle";

    /// <summary>Latest live detail line from the running stage (status API
    /// surface; null while no pipeline runs).</summary>
    public string? CurrentDetail { get; private set; }

    public DateTime? LastRunAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Moves the pipeline to a named stage (core calls this).</summary>
    public void SetStage(string stage) => CurrentStage = stage;

    /// <summary>Publishes a live detail line of the running stage.</summary>
    public void SetDetail(string? detail) => CurrentDetail = detail;

    /// <summary>
    /// Runs one attempt: acquires the lock (InvalidOperationException when
    /// busy), executes the core, records the error text and the wall time,
    /// resets the stage. Exceptions propagate untouched - mapping them onto
    /// a site health verdict is the caller's business.
    /// </summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> core, CancellationToken cancellationToken)
    {
        if (!await _lock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException(_busyMessage);
        }

        try
        {
            var result = await core(cancellationToken);
            LastError = null;
            return result;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            throw;
        }
        finally
        {
            LastRunAt = DateTime.UtcNow;
            CurrentStage = "idle";
            CurrentDetail = null;
            _lock.Release();
        }
    }
}
