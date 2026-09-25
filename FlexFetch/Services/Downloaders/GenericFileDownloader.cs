using System.Net;
using System.Net.Http.Headers;
using FlexFetch.Config;
using FlexFetch.Entities;
using FlexFetch.Services;
using FlexFetch.Services.Downloaders;
using FlexFetch.Services.Routing;
using Serilog;
using ILogger = Serilog.ILogger;

namespace FlexFetch.Services.Downloaders;

/// <summary>
/// Generic file downloader: direct http/https links, filename inference
/// (Content-Disposition -> URL path), resumable downloads (Range), and a
/// retry with Referrer on 403/401. This is the fallback downloader.
/// </summary>
public sealed class GenericFileDownloader : IDownloader
{
    private readonly IProxyService _proxy;
    private readonly StorageService _storage;
    private readonly IConfiguration _config;
    private readonly ILogger _log;

    public GenericFileDownloader(IProxyService proxy, StorageService storage, IConfiguration config, ILogger log)
    {
        _proxy = proxy;
        _storage = storage;
        _config = config;
        _log = log;
    }

    public string Type => "Generic";

    public bool IsDomainSpecific => false;

    public bool CanHandle(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public Task<AnalysisResult> AnalyzeAsync(string url, string taskId, CancellationToken cancellationToken)
    {
        var fileName = FileNameRules.Sanitize(FileNameRules.InferFromUrl(url));
        return Task.FromResult(new AnalysisResult
        {
            Title = fileName,
            DirectUrl = url,
            SuggestedFileName = fileName,
        });
    }

    public async Task DownloadAsync(TaskItem task, AnalysisResult analysis, Action<double> progress, CancellationToken cancellationToken)
    {
        var url = new Uri(analysis.DirectUrl ?? task.Url);
        var handler = _proxy.CreateHandler(url);
        using var client = new HttpClient(handler) { Timeout = GetTimeout() };

        // Send Referrer when provided (and always attach the original URL on 403 retry).
        var referrer = analysis.Referrer ?? task.Referrer;
        if (referrer is not null)
        {
            client.DefaultRequestHeaders.Referrer = new Uri(referrer);
        }

        _storage.EnsureTaskDir(task.Id);
        var partPath = _storage.GetTaskFilePath(task.Id, $"{task.Id}.part");
        var resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        HttpResponseMessage response;
        try
        {
            response = await SendWithReferrerRetryAsync(client, url, resumeFrom, analysis, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Connection-level failure (DNS, refused, proxy): transient.
            throw new RetryableException($"Connection failed for {url}: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient timeout (not a user cancellation): transient.
            throw new RetryableException($"Request timed out for {url}");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                // 5xx / 429 may recover on retry; other 4xx are deterministic.
                throw status >= 500 || status == 429
                    ? new RetryableException($"HTTP {status} for {url}")
                    : new InvalidOperationException($"HTTP {status} for {url}");
            }

            if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                // Server ignored the Range request; start over.
                resumeFrom = 0;
            }

            var contentLength = response.Content.Headers.ContentLength ?? 0;
            var totalLength = resumeFrom + contentLength;
            task.FileSize = totalLength;

            var fileName = ResolveFileName(response, analysis, url, task.FileName);
            task.FileName = fileName;
            _log.Information("Downloading {Url} to {File} ({Total} bytes)", url, fileName, totalLength);

            await using (var fileStream = new FileStream(
                partPath,
                resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[16 * 1024];
                long written = resumeFrom;
                int bytesRead;
                while ((bytesRead = await responseStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    written += bytesRead;
                    if (totalLength > 0)
                    {
                        progress(Math.Min(1.0, (double)written / totalLength));
                    }
                }
            }

            // Move the finished part file to its final name.
            var finalPath = _storage.GetTaskFilePath(task.Id, fileName);
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }
            File.Move(partPath, finalPath);
        }
    }

    private async Task<HttpResponseMessage> SendWithReferrerRetryAsync(
        HttpClient client,
        Uri url,
        long resumeFrom,
        AnalysisResult analysis,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        }

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // 403/401 with source-page validation: retry once with the source page as Referrer.
        if ((response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
            && client.DefaultRequestHeaders.Referrer is null)
        {
            response.Dispose();
            client.DefaultRequestHeaders.Referrer = url;
            _log.Warning("Got {Status} for {Url}, retrying with Referrer", response.StatusCode, url);
            var retryRequest = new HttpRequestMessage(HttpMethod.Get, url);
            if (resumeFrom > 0)
            {
                retryRequest.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            }
            return await client.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        return response;
    }

    private static string ResolveFileName(HttpResponseMessage response, AnalysisResult analysis, Uri url, string? taskFileName)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var headerName = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
        if (!string.IsNullOrWhiteSpace(headerName))
        {
            return FileNameRules.Sanitize(headerName);
        }

        // The task's own filename (e.g. the title set when a child task was
        // submitted from analysis) beats the URL-inferred fallback name.
        if (!string.IsNullOrWhiteSpace(taskFileName))
        {
            return FileNameRules.Sanitize(taskFileName);
        }

        if (!string.IsNullOrWhiteSpace(analysis.SuggestedFileName))
        {
            return FileNameRules.Sanitize(analysis.SuggestedFileName);
        }

        return FileNameRules.Sanitize(FileNameRules.InferFromUrl(url.ToString()));
    }

    private TimeSpan GetTimeout()
    {
        var raw = ConfigRegistry.From(_config, ConfigKeys.TimeoutSeconds);
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(60);
    }
}
