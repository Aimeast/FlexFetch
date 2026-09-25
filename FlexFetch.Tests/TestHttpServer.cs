using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FlexFetch.Tests;

/// <summary>
/// Minimal in-process HTTP server for downloader tests (no admin / ACL needed).
/// Supports GET, Range, Content-Disposition and records all requests.
/// </summary>
public sealed class TestHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptTask;
    private readonly Func<HttpRequest, HttpResponse> _handler;

    public TestHttpServer(Func<HttpRequest, HttpResponse> handler)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _handler = handler;
        _acceptTask = AcceptLoopAsync();
    }

    private int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public List<HttpRequest> Requests { get; } = new();

    public sealed record HttpRequest(Dictionary<string, string> Headers);

    public sealed record HttpResponse(int Status, byte[] Body, Dictionary<string, string>? Headers = null);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var head = await ReadHeadAsync(stream);
                if (head is null)
                {
                    return;
                }

                var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var idx = line.IndexOf(':');
                    if (idx > 0)
                    {
                        headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
                    }
                }

                var request = new HttpRequest(headers);
                lock (Requests)
                {
                    Requests.Add(request);
                }

                var response = _handler(request);
                await WriteResponseAsync(stream, response);
            }
        }
        catch (IOException)
        {
            // Client disconnected; ignore.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return sb.Length > 0 ? sb.ToString() : null;
            }

            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (sb.ToString().Contains("\r\n\r\n"))
            {
                return sb.ToString();
            }
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpResponse response)
    {
        var reason = response.Status switch
        {
            200 => "OK",
            206 => "Partial Content",
            403 => "Forbidden",
            401 => "Unauthorized",
            _ => "OK",
        };

        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {response.Status} {reason}\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append($"Content-Length: {response.Body.Length}\r\n");
        if (response.Headers is not null)
        {
            foreach (var (key, value) in response.Headers)
            {
                sb.Append($"{key}: {value}\r\n");
            }
        }

        sb.Append("\r\n");
        var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headBytes);
        await stream.WriteAsync(response.Body);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }
}
