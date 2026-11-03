using System.Net;
using System.Net.Sockets;
using VpnUs.Core.Models;
using VpnUs.Core.Net;
using Xunit;

namespace VpnUs.Core.Tests;

public class ServerPingServiceTests
{
    [Fact]
    public async Task PingAsync_ReturnsDelay_WhenPortAcceptsTcp()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accept = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
            }
        });

        try
        {
            var delay = await ServerPingService.PingAsync("127.0.0.1", port, timeoutMs: 2000);

            Assert.NotNull(delay);
            Assert.InRange(delay.Value, 1, 2000);
        }
        finally
        {
            listener.Stop();
            await accept;
        }
    }

    [Fact]
    public async Task PingAsync_ReturnsNull_WhenPortIsClosed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var delay = await ServerPingService.PingAsync("127.0.0.1", port, timeoutMs: 1500);

        Assert.Null(delay);
    }

    [Theory]
    [InlineData(null, 443)]
    [InlineData("", 443)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 70000)]
    public async Task PingAsync_ReturnsNull_ForInvalidEndpoint(string? host, int port)
    {
        var delay = await ServerPingService.PingAsync(host, port, timeoutMs: 500);

        Assert.Null(delay);
    }

    [Fact]
    public async Task PingAsync_Node_UsesIcmpForUdpProtocols()
    {
        var node = new ServerNode
        {
            Type = "hysteria2",
            Server = "127.0.0.1",
            ServerPort = 65500,
        };

        var delay = await ServerPingService.PingAsync(node, timeoutMs: 2000);

        Assert.NotNull(delay);
        Assert.True(delay >= 1);
    }
}
