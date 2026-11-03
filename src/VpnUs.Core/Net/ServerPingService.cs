using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using VpnUs.Core.Models;

namespace VpnUs.Core.Net;

/// <summary>
/// TCP-пинг сервера без подключения к туннелю. Измеряет время установки TCP-соединения
/// до порта, который слушает VPN-ядро (VLESS/Reality/Trojan и т.п.).
/// </summary>
public static class ServerPingService
{
    public const int DefaultTimeoutMs = 3000;

    public static async Task<int?> PingAsync(
        string? host,
        int port,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port is <= 0 or > 65535)
        {
            return null;
        }

        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Max(1, timeoutMs));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(host.Trim(), port, timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var delay = (int)Math.Round(stopwatch.Elapsed.TotalMilliseconds);
            return Math.Max(1, delay);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }

    public static Task<int?> PingAsync(
        ServerNode? node,
        int timeoutMs = DefaultTimeoutMs,
        CancellationToken ct = default)
    {
        if (node is null)
        {
            return Task.FromResult<int?>(null);
        }

        // Hysteria2 и TUIC работают поверх UDP — TCP-порт у них не отвечает,
        // поэтому для них измеряем ICMP-задержку до хоста.
        return UsesUdp(node.Type)
            ? PingIcmpAsync(node.Server, timeoutMs, ct)
            : PingAsync(node.Server, node.ServerPort, timeoutMs, ct);
    }

    private static bool UsesUdp(string? type)
        => string.Equals(type, "hysteria2", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(type, "tuic", StringComparison.OrdinalIgnoreCase);

    private static async Task<int?> PingIcmpAsync(string? host, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        try
        {
            using var ping = new Ping();
            var reply = await ping
                .SendPingAsync(host.Trim(), Math.Max(1, timeoutMs))
                .WaitAsync(ct)
                .ConfigureAwait(false);

            return reply.Status == IPStatus.Success ? Math.Max(1, (int)reply.RoundtripTime) : null;
        }
        catch (Exception ex) when (ex is PingException or SocketException or ArgumentException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }
}
