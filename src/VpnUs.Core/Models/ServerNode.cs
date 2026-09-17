using System.Text.Json.Nodes;

namespace VpnUs.Core.Models;

public sealed class ServerNode
{
    public string Tag { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Server { get; set; } = "";
    public int ServerPort { get; set; }
    public JsonObject Outbound { get; set; } = new();

    public string Display => string.IsNullOrWhiteSpace(Name) ? $"{Server}:{ServerPort}" : Name;

    public string Endpoint => ServerPort is > 0 and <= 65535 ? $"{Server}:{ServerPort}" : Server;

    public string FingerprintSource => $"{Type}|{Server}|{ServerPort}|{Name}";
}

public sealed class AppEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsRunning { get; set; }
    public bool Selected { get; set; }
    public string? Publisher { get; set; }

    public bool CanSelect => !string.IsNullOrWhiteSpace(Path) && Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}

public sealed class SubscriptionFetchResult
{
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public int Redirects { get; set; }
    public string Body { get; set; } = "";
    public string? Error { get; set; }
    public string EffectiveUrl { get; set; } = "";
}

public sealed class SubscriptionParseResult
{
    public List<ServerNode> Nodes { get; set; } = [];
    public int Skipped { get; set; }
    public string? Error { get; set; }
}
