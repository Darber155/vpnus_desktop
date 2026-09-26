using System.Text;
using System.Text.Json.Nodes;

namespace VpnUs.Core.Models;

public sealed class ServerNode
{
    private string _name = "";

    public string Tag { get; set; } = "";

    public string Name
    {
        get => _name;
        set => _name = FixMojibake(value);
    }

    public string Type { get; set; } = "";
    public string Server { get; set; } = "";
    public int ServerPort { get; set; }
    public JsonObject Outbound { get; set; } = new();

    public string Display => string.IsNullOrWhiteSpace(Name) ? $"{Server}:{ServerPort}" : Name;

    public string Endpoint => ServerPort is > 0 and <= 65535 ? $"{Server}:{ServerPort}" : Server;

    public string FingerprintSource => $"{Type}|{Server}|{ServerPort}|{Name}";

    public static string FixMojibake(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? "";
        }

        var allUnder256 = true;
        var hasHighByte = false;
        foreach (var ch in text)
        {
            if (ch > 0xFF)
            {
                allUnder256 = false;
                break;
            }

            if (ch >= 0x80)
            {
                hasHighByte = true;
            }
        }

        if (allUnder256 && hasHighByte)
        {
            try
            {
                var bytes = new byte[text.Length];
                for (var i = 0; i < text.Length; i++)
                {
                    bytes[i] = (byte)text[i];
                }

                var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
                var decoded = utf8.GetString(bytes);
                if (decoded != text && !decoded.Contains('\uFFFD'))
                {
                    return decoded;
                }
            }
            catch (DecoderFallbackException)
            {
            }
        }

        return text;
    }
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
