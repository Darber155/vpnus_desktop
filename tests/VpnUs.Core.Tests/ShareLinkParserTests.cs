using System.Text;
using System.Text.Json.Nodes;
using VpnUs.Core.Config;
using VpnUs.Core.Models;
using VpnUs.Core.Subscription;
using Xunit;

namespace VpnUs.Core.Tests;

public class ShareLinkParserTests
{
    private const string VlessReality =
        "vless://11111111-2222-3333-4444-555555555555@1.2.3.4:443?security=reality&sni=www.microsoft.com&fp=chrome&pbk=abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG&sid=0123456789abcdef&type=tcp&flow=xtls-rprx-vision#Reality%20DE";

    private const string VlessWs =
        "vless://11111111-2222-3333-4444-555555555555@1.2.3.5:443?security=tls&sni=cdn.example.com&type=ws&path=%2Fws&host=cdn.example.com&fp=firefox#WS-Node";

    private const string VlessGrpc =
        "vless://11111111-2222-3333-4444-555555555555@1.2.3.6:443?type=grpc&serviceName=grpcsvc&security=tls&sni=g.example.com#gRPC";

    private const string VlessXhttp =
        "vless://11111111-2222-3333-4444-555555555555@1.2.3.7:443?type=xhttp&security=tls#XHTTP";

    private const string Trojan =
        "trojan://pa%24%24word@1.2.3.9:443?security=tls&sni=t.example.com&type=grpc&serviceName=tsvc#Trojan";

    private const string SsSip002 =
        "ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ=@1.2.3.10:8388#SS-SIP002";

    private const string Hy2 =
        "hysteria2://pw@1.2.3.12:8443?sni=h2.example.com&insecure=1&obfs=salamander&obfs-password=op#Hysteria2";

    private const string Tuic =
        "tuic://11111111-2222-3333-4444-555555555555:pass@1.2.3.13:443?sni=t.example.com&congestion_control=bbr#TUIC";

    private const string Anytls =
        "anytls://pw@1.2.3.14:443?sni=a.example.com#AnyTLS";

    private static string VmessLink()
    {
        const string json =
            """{"v":"2","ps":"VMess WS","add":"1.2.3.8","port":"8080","id":"11111111-2222-3333-4444-555555555555","aid":"0","scy":"auto","net":"ws","type":"none","host":"example.org","path":"/vm","tls":"tls","sni":"example.org"}""";
        return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static string SsLegacyLink()
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-128-gcm:pw@1.2.3.11:8388"));
        return "ss://" + raw + "#SS-Legacy";
    }

    public static string SamplePlainBody() => string.Join('\n',
    [
        VlessReality,
        VlessWs,
        VlessGrpc,
        VlessXhttp,
        VmessLink(),
        Trojan,
        SsSip002,
        SsLegacyLink(),
        Hy2,
        Tuic,
        Anytls,
        "not-a-link",
        "# comment",
        "",
    ]);

    [Fact]
    public void ParseSubscription_UsesUrlSafeBase64WithoutPadding()
    {
        var plain = SamplePlainBody();
        var standard = Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        var urlSafe = standard.Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var result = ShareLinkParser.ParseSubscription(urlSafe);

        Assert.Null(result.Error);
        Assert.Equal(10, result.Nodes.Count);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void ParseSubscription_SkipsUnsupportedTransports()
    {
        var result = ShareLinkParser.ParseSubscription(VlessXhttp);

        Assert.Empty(result.Nodes);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ParseSubscription_EmptyBody_ReportsError()
    {
        var result = ShareLinkParser.ParseSubscription("   \n  ");

        Assert.Empty(result.Nodes);
        Assert.Equal("Пустое тело подписки", result.Error);
    }

    [Fact]
    public void ParseVlessReality_MapsTlsRealityAndFlow()
    {
        var node = ShareLinkParser.ParseLink(VlessReality);

        Assert.NotNull(node);
        Assert.Equal("vless", node!.Type);
        Assert.Equal("1.2.3.4", node.Server);
        Assert.Equal(443, node.ServerPort);
        Assert.Equal("Reality DE", node.Name);

        var outbound = node.Outbound;
        Assert.Equal("xtls-rprx-vision", outbound["flow"]!.GetValue<string>());

        var tls = outbound["tls"]!.AsObject();
        Assert.True(tls["enabled"]!.GetValue<bool>());
        Assert.Equal("www.microsoft.com", tls["server_name"]!.GetValue<string>());
        Assert.Equal("chrome", tls["utls"]!["fingerprint"]!.GetValue<string>());
        Assert.True(tls["reality"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG", tls["reality"]!["public_key"]!.GetValue<string>());
        Assert.Equal("0123456789abcdef", tls["reality"]!["short_id"]!.GetValue<string>());
    }

    [Fact]
    public void ParseVlessWs_MapsTransportAndHeaders()
    {
        var node = ShareLinkParser.ParseLink(VlessWs)!;
        var transport = node.Outbound["transport"]!.AsObject();

        Assert.Equal("ws", transport["type"]!.GetValue<string>());
        Assert.Equal("/ws", transport["path"]!.GetValue<string>());
        Assert.Equal("cdn.example.com", transport["headers"]!["Host"]!.GetValue<string>());
    }

    [Fact]
    public void ParseVlessGrpc_MapsServiceName()
    {
        var node = ShareLinkParser.ParseLink(VlessGrpc)!;
        Assert.Equal("grpcsvc", node.Outbound["transport"]!["service_name"]!.GetValue<string>());
    }

    [Fact]
    public void ParseVmess_DecodesBase64Json()
    {
        var node = ShareLinkParser.ParseLink(VmessLink())!;

        Assert.Equal("vmess", node.Type);
        Assert.Equal("VMess WS", node.Name);
        Assert.Equal("11111111-2222-3333-4444-555555555555", node.Outbound["uuid"]!.GetValue<string>());
        Assert.Equal(8080, node.Outbound["server_port"]!.GetValue<int>());
        Assert.Equal("auto", node.Outbound["security"]!.GetValue<string>());
        Assert.Equal("ws", node.Outbound["transport"]!["type"]!.GetValue<string>());
        Assert.True(node.Outbound["tls"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void ParseTrojan_DecodesPasswordAndTls()
    {
        var node = ShareLinkParser.ParseLink(Trojan)!;

        Assert.Equal("trojan", node.Type);
        Assert.Equal("pa$$word", node.Outbound["password"]!.GetValue<string>());
        Assert.Equal("t.example.com", node.Outbound["tls"]!["server_name"]!.GetValue<string>());
        Assert.Equal("tsvc", node.Outbound["transport"]!["service_name"]!.GetValue<string>());
    }

    [Fact]
    public void ParseShadowsocks_SupportsBothFormats()
    {
        var sip002 = ShareLinkParser.ParseLink(SsSip002)!;
        Assert.Equal("shadowsocks", sip002.Type);
        Assert.Equal("aes-256-gcm", sip002.Outbound["method"]!.GetValue<string>());
        Assert.Equal("password", sip002.Outbound["password"]!.GetValue<string>());

        var legacy = ShareLinkParser.ParseLink(SsLegacyLink())!;
        Assert.Equal("aes-128-gcm", legacy.Outbound["method"]!.GetValue<string>());
        Assert.Equal("pw", legacy.Outbound["password"]!.GetValue<string>());
        Assert.Equal(8388, legacy.Outbound["server_port"]!.GetValue<int>());
    }

    [Fact]
    public void ParseHysteria2_MapsObfsAndTls()
    {
        var node = ShareLinkParser.ParseLink(Hy2)!;

        Assert.Equal("hysteria2", node.Type);
        Assert.Equal("salamander", node.Outbound["obfs"]!["type"]!.GetValue<string>());
        Assert.Equal("op", node.Outbound["obfs"]!["password"]!.GetValue<string>());
        Assert.True(node.Outbound["tls"]!["insecure"]!.GetValue<bool>());
    }

    [Fact]
    public void ParseTuic_MapsCongestionControl()
    {
        var node = ShareLinkParser.ParseLink(Tuic)!;

        Assert.Equal("tuic", node.Type);
        Assert.Equal("bbr", node.Outbound["congestion_control"]!.GetValue<string>());
        Assert.Equal("h3", node.Outbound["tls"]!["alpn"]![0]!.GetValue<string>());
    }

    [Fact]
    public void ParseAnytls_Works()
    {
        var node = ShareLinkParser.ParseLink(Anytls)!;
        Assert.Equal("anytls", node.Type);
        Assert.Equal("a.example.com", node.Outbound["tls"]!["server_name"]!.GetValue<string>());
    }

    [Fact]
    public void ParseSubscription_ProducesStableTags()
    {
        var first = ShareLinkParser.ParseSubscription(VlessReality).Nodes[0].Tag;
        var second = ShareLinkParser.ParseSubscription(VlessReality).Nodes[0].Tag;

        Assert.Equal(first, second);
        Assert.StartsWith("n-", first);
    }

    [Fact]
    public void ParseSubscription_IgnoresDuplicateLinks()
    {
        var result = ShareLinkParser.ParseSubscription(VlessReality + "\n" + VlessReality + "\n" + VlessWs);
        Assert.Equal(2, result.Nodes.Count);
    }

    [Theory]
    [InlineData("vless://not-a-uuid@1.2.3.4:443?security=tls#bad")]
    [InlineData("tuic://nope:pass@1.2.3.4:443#bad")]
    [InlineData("vmess://eyJ2IjoiMiIsImFkZCI6IjEuMi4zLjQiLCJwb3J0IjoiNDQzIiwiaWQiOiJiYWQtaWQiLCJuZXQiOiJ0Y3AifQ==")]
    public void ParseLink_SkipsInvalidUuid(string link)
    {
        Assert.Null(ShareLinkParser.ParseLink(link));
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555", true)]
    [InlineData("11111111-2222-3333-4444-55555555555Z", false)]
    [InlineData("11111111222233334444555555555555", false)]
    [InlineData("", false)]
    public void IsUuid_Validates(string value, bool expected)
    {
        Assert.Equal(expected, ShareLinkParser.IsUuid(value));
    }
}
