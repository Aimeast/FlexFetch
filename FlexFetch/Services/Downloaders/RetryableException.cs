namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Thrown when a download attempt failed for a transient, retryable reason
/// (timeout, connection failure, proxy fault, server 5xx, rate limiting).
/// The task service retries these; deterministic errors are not wrapped in
/// this exception and fail the task immediately.
/// </summary>
public sealed class RetryableException : Exception
{
    public RetryableException(string message)
        : base(message)
    {
    }

    public RetryableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
