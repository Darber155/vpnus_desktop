using System.Net;
using System.Net.Sockets;
using System.Text;
using VpnUs.Core.Subscription;
using Xunit;

namespace VpnUs.Core.Tests;

internal sealed class HttpRequestInfo
{
    public string Method { get; init; } = "";

    public string Path { get; init; } = "";

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class HttpResponse
{
    public int StatusCode { get; init; } = 200;

    public string Reason { get; init; } = "OK";

    public string Body { get; init; } = "";

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Минимальный HTTP-сервер на TcpListener (не требует прав на HttpListener).</summary>
internal sealed class FakeHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<HttpRequestInfo, HttpResponse> _handler;
    private readonly CancellationTokenSource _cts = new();

    public FakeHttpServer(Func<HttpRequestInfo, HttpResponse> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public List<HttpRequestInfo> Requests { get; } = [];

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                string? line;
                var info = new HttpRequestInfo { Method = requestLine.Split(' ')[0], Path = requestLine.Split(' ')[1] };
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0)
                    {
                        info.Headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                    }
                }

                lock (Requests)
                {
                    Requests.Add(info);
                }

                var response = _handler(info);
                var body = Encoding.UTF8.GetBytes(response.Body);

                var head = new StringBuilder();
                head.Append($"HTTP/1.1 {response.StatusCode} {response.Reason}\r\n");
                foreach (var (key, value) in response.Headers)
                {
                    head.Append($"{key}: {value}\r\n");
                }

                head.Append($"Content-Length: {body.Length}\r\n");
                head.Append("Connection: close\r\n\r\n");

                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}

public class SubscriptionClientTests
{
    private static string Base64Body()
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(ShareLinkParserTests.SamplePlainBody()));

    [Fact]
    public async Task Fetch_SendsV2rayNgUserAgent()
    {
        using var server = new FakeHttpServer(_ => new HttpResponse { Body = Base64Body() });
        using var client = new SubscriptionClient();

        var result = await client.FetchAsync(server.BaseUrl + "/sub");

        Assert.True(result.Success);
        Assert.Equal("v2rayNG/1.8.5", server.Requests[0].Headers["User-Agent"]);
    }

    [Fact]
    public async Task Fetch_RepeatsSameUrl_When307ComesWithoutLocationButWithCookie()
    {
        using var server = new FakeHttpServer(request =>
        {
            if (!request.Headers.TryGetValue("Cookie", out var cookie) || !cookie.Contains("session=abc"))
            {
                var redirect = new HttpResponse { StatusCode = 307, Reason = "Temporary Redirect" };
                redirect.Headers["Set-Cookie"] = "session=abc; Path=/";
                return redirect;
            }

            if (request.Headers["User-Agent"] != "v2rayNG/1.8.5")
            {
                return new HttpResponse { StatusCode = 403, Reason = "Forbidden" };
            }

            return new HttpResponse { Body = Base64Body() };
        });

        using var client = new SubscriptionClient();
        var result = await client.FetchAsync(server.BaseUrl + "/sub");

        Assert.True(result.Success, result.Error);
        Assert.True(result.Redirects >= 1);
        Assert.Equal(2, server.Requests.Count);

        var parsed = ShareLinkParser.ParseSubscription(result.Body);
        Assert.Equal(10, parsed.Nodes.Count);
    }

    [Fact]
    public async Task Fetch_FollowsRelativeLocation()
    {
        using var server = new FakeHttpServer(request =>
        {
            if (request.Path == "/sub")
            {
                var redirect = new HttpResponse { StatusCode = 302, Reason = "Found" };
                redirect.Headers["Location"] = "/final";
                return redirect;
            }

            return new HttpResponse { Body = Base64Body() };
        });

        using var client = new SubscriptionClient();
        var result = await client.FetchAsync(server.BaseUrl + "/sub");

        Assert.True(result.Success, result.Error);
        Assert.Equal("/final", server.Requests[^1].Path);
    }

    [Fact]
    public async Task Fetch_ReturnsError_OnHttpFailure()
    {
        using var server = new FakeHttpServer(_ => new HttpResponse { StatusCode = 500, Reason = "Server Error" });
        using var client = new SubscriptionClient();

        var result = await client.FetchAsync(server.BaseUrl + "/sub");

        Assert.False(result.Success);
        Assert.Contains("500", result.Error);
    }

    [Fact]
    public async Task Fetch_ReturnsError_OnInvalidUrl()
    {
        using var client = new SubscriptionClient();
        var result = await client.FetchAsync("not a url");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}
