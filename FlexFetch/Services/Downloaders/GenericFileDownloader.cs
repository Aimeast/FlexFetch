using System.Net;
using System.Net.Http.Headers;
using FlexFetch.Config;
using FlexFetch.Data;
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
    private readonly IConfigRepository _config;
    private readonly ILogger _log;

    public GenericFileDownloader(IProxyService proxy, StorageService storage, IConfigRepository config, ILogger log)
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

        using var response = await SendWithReferrerRetryAsync(client, url, resumeFrom, analysis, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            // Server ignored the Range request; start over.
            resumeFrom = 0;
        }

        var contentLength = response.Content.Headers.ContentLength ?? 0;
        var totalLength = resumeFrom + contentLength;
        task.FileSize = totalLength;

        var fileName = ResolveFileName(response, analysis, url);
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

    private static string ResolveFileName(HttpResponseMessage response, AnalysisResult analysis, Uri url)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var headerName = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
        if (!string.IsNullOrWhiteSpace(headerName))
        {
            return FileNameRules.Sanitize(headerName);
        }

        if (!string.IsNullOrWhiteSpace(analysis.SuggestedFileName))
        {
            return FileNameRules.Sanitize(analysis.SuggestedFileName);
        }

        return FileNameRules.Sanitize(FileNameRules.InferFromUrl(url.ToString()));
    }

    private TimeSpan GetTimeout()
    {
        var raw = _config.Get(ConfigKeys.TimeoutSeconds) ?? ConfigRegistry.GetDefault(ConfigKeys.TimeoutSeconds);
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(60);
    }
}
