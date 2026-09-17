using System.Text.Json;
using System.Text.Json.Nodes;

namespace VpnUs.Core.Clash;

public sealed class ClashProxyInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Now { get; set; } = "";
    public List<string> All { get; set; } = [];
    public int? Delay { get; set; }
}

public sealed class ClashTraffic
{
    public long Download { get; set; }
    public long Upload { get; set; }
    public int ConnectionCount { get; set; }
}

public sealed class ClashConnection
{
    public string Id { get; set; } = "";
    public long Upload { get; set; }
    public long Download { get; set; }
    public List<string> Chains { get; set; } = [];
    public string Host { get; set; } = "";
    public string ProcessPath { get; set; } = "";

    /// <summary>true, если соединение ушло в proxy (а не direct/block).</summary>
    public bool IsProxy => Chains.Exists(c =>
        c.Length > 0 &&
        !c.Equals("direct", StringComparison.OrdinalIgnoreCase) &&
        !c.Equals("block", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Клиент Clash API sing-box (experimental.clash_api, 127.0.0.1).</summary>
public sealed class ClashApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public ClashApiClient(int port = 9090)
    {
        Port = port;
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public int Port { get; }

    /// <summary>Последний GET завершился сетевой ошибкой (Clash API не ответил).</summary>
    public bool LastRequestFailed { get; private set; }

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        var root = await GetJsonAsync("version", ct).ConfigureAwait(false);
        return root?["version"]?.GetValue<string>() is { Length: > 0 } v ? v : null;
    }

    public async Task<Dictionary<string, ClashProxyInfo>> GetProxiesAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, ClashProxyInfo>(StringComparer.Ordinal);
        var root = await GetJsonAsync("proxies", ct).ConfigureAwait(false);
        if (root?["proxies"] is not JsonObject proxies)
        {
            return result;
        }

        foreach (var (name, node) in proxies)
        {
            if (node is not JsonObject p)
            {
                continue;
            }

            var info = new ClashProxyInfo
            {
                Name = name,
                Type = p["type"]?.GetValue<string>() ?? "",
                Now = p["now"]?.GetValue<string>() ?? "",
            };

            if (p["all"] is JsonArray all)
            {
                foreach (var item in all)
                {
                    if (item?.GetValue<string>() is { Length: > 0 } tag)
                    {
                        info.All.Add(tag);
                    }
                }
            }

            if (p["history"] is JsonArray history && history.Count > 0 && history[^1] is JsonObject last)
            {
                info.Delay = last["delay"]?.GetValue<int>();
            }

            result[name] = info;
        }

        return result;
    }

    public async Task<int?> GetDelayAsync(string proxyTag, string url, int timeoutMs = 5000, CancellationToken ct = default)
    {
        var root = await GetJsonAsync(
            $"proxies/{Uri.EscapeDataString(proxyTag)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMs}",
            ct).ConfigureAwait(false);

        if (root?["delay"] is JsonValue value && value.TryGetValue(out int delay))
        {
            return delay;
        }

        return null;
    }

    public async Task<ClashTraffic?> GetTrafficAsync(CancellationToken ct = default)
    {
        var root = await GetJsonAsync("connections", ct).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        var traffic = new ClashTraffic
        {
            Download = root["downloadTotal"]?.GetValue<long>() ?? 0,
            Upload = root["uploadTotal"]?.GetValue<long>() ?? 0,
        };

        if (root["connections"] is JsonArray connections)
        {
            traffic.ConnectionCount = connections.Count;
        }

        return traffic;
    }

    public async Task<IReadOnlyList<ClashConnection>> GetConnectionsAsync(CancellationToken ct = default)
    {
        var result = new List<ClashConnection>();
        var root = await GetJsonAsync("connections", ct).ConfigureAwait(false);
        if (root?["connections"] is not JsonArray connections)
        {
            return result;
        }

        foreach (var item in connections.OfType<JsonObject>())
        {
            var connection = new ClashConnection
            {
                Id = item["id"]?.GetValue<string>() ?? "",
                Upload = item["upload"]?.GetValue<long>() ?? 0,
                Download = item["download"]?.GetValue<long>() ?? 0,
            };

            if (item["chains"] is JsonArray chains)
            {
                foreach (var chain in chains)
                {
                    if (chain?.GetValue<string>() is { Length: > 0 } tag)
                    {
                        connection.Chains.Add(tag);
                    }
                }
            }

            if (item["metadata"] is JsonObject metadata)
            {
                connection.Host = metadata["host"]?.GetValue<string>() ?? "";
                connection.ProcessPath = metadata["processPath"]?.GetValue<string>() ?? "";
            }

            result.Add(connection);
        }

        return result;
    }

    public async Task<bool> SelectAsync(string groupTag, string proxyTag, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"proxies/{Uri.EscapeDataString(groupTag)}")
            {
                Content = new StringContent($"{{\"name\":{JsonSerializer.Serialize(proxyTag)}}}", System.Text.Encoding.UTF8, "application/json"),
            };

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<bool> SetModeAsync(string mode, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, "configs")
            {
                Content = new StringContent($"{{\"mode\":{JsonSerializer.Serialize(mode)}}}", System.Text.Encoding.UTF8, "application/json"),
            };

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<JsonObject?> GetJsonAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastRequestFailed = true;
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            LastRequestFailed = false;
            return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LastRequestFailed = true;
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
