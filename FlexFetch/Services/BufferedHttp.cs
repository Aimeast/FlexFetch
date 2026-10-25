namespace FlexFetch.Services;

/// <summary>
/// Sends a GET and buffers the response before the caller-owned client and
/// handler are disposed. Returning a live response from a method that
/// disposes its HttpClient is unsafe: the content stream may already be
/// aborted. The buffered copy carries the status, the headers (Set-Cookie
/// included) and the body, so it stays readable for the caller.
/// </summary>
public static class BufferedHttp
{
    public static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            uri, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var buffered = new HttpResponseMessage(response.StatusCode)
        {
            Content = new ByteArrayContent(body),
        };
        foreach (var header in response.Headers)
        {
            buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in response.Content.Headers)
        {
            buffered.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return buffered;
    }
}
