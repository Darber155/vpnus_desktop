using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VpnUs.Core.Models;
using VpnUs.Core.Text;

namespace VpnUs.Core.Subscription;

/// <summary>
/// C# port of HappWrt parse.uc: share-link and base64 subscription parser for sing-box.
/// </summary>
public static class ShareLinkParser
{
    public static SubscriptionParseResult ParseSubscription(string body)
    {
        var result = new SubscriptionParseResult();
        if (string.IsNullOrWhiteSpace(body))
        {
            result.Error = "Пустое тело подписки";
            return result;
        }

        var content = DecodeSubscription(body);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usedTags = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<ServerNode>();

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || !line.Contains("://", StringComparison.Ordinal))
            {
                continue;
            }

            var node = ParseLink(line);
            if (node is null)
            {
                result.Skipped++;
                continue;
            }

            if (!seen.Add(node.FingerprintSource))
            {
                continue;
            }

            node.Tag = MakeTag(node, usedTags);
            node.Outbound["tag"] = node.Tag;
            nodes.Add(node);
        }

        result.Nodes = nodes;
        if (nodes.Count == 0)
        {
            result.Error = "Не найдено поддерживаемых серверов";
        }

        return result;
    }

    public static string DecodeSubscription(string body)
    {
        var b = body.Trim();
        if (b.Length == 0)
        {
            return "";
        }

        if (b.Contains("://", StringComparison.Ordinal))
        {
            return b;
        }

        var decoded = Base64Url.TryDecodeOrNull(b);
        if (decoded is not null && decoded.Contains("://", StringComparison.Ordinal))
        {
            return decoded;
        }

        return b;
    }

    public static ServerNode? ParseLink(string uri)
    {
        var link = ParseLinkParts(uri);
        if (link is null)
        {
            return null;
        }

        return link.Scheme switch
        {
            "vless" => ParseVless(link),
            "vmess" => ParseVmess(link),
            "trojan" => ParseTrojan(link),
            "ss" => ParseShadowsocks(link),
            "hysteria2" or "hy2" => ParseHysteria2(link),
            "tuic" => ParseTuic(link),
            "anytls" => ParseAnytls(link),
            _ => null,
        };
    }

    private static string MakeTag(ServerNode node, HashSet<string> used)
    {
        var bytes = Encoding.UTF8.GetBytes(node.FingerprintSource);
        var hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant()[..8];
        var tag = "n-" + hash;
        var i = 2;
        while (!used.Add(tag))
        {
            tag = $"n-{hash}-{i++}";
        }

        return tag;
    }

    /// <summary>UUID в форме 8-4-4-4-12; sing-box не стартует с некорректным UUID,
    /// поэтому такие узлы пропускаем ещё на этапе парсинга.</summary>
    public static bool IsUuid(string? value)
    {
        if (value is null || value.Length != 36)
        {
            return false;
        }

        for (var i = 0; i < 36; i++)
        {
            var c = value[i];
            if (i is 8 or 13 or 18 or 23)
            {
                if (c != '-')
                {
                    return false;
                }

                continue;
            }

            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record LinkParts(string Scheme, string Rest, Dictionary<string, string> Query, string Name);

    private static LinkParts? ParseLinkParts(string uri)
    {
        var sep = uri.IndexOf("://", StringComparison.Ordinal);
        if (sep < 0)
        {
            return null;
        }

        var scheme = uri[..sep].ToLowerInvariant();
        var rest = uri[(sep + 3)..];
        var name = "";

        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            name = Pct(rest[(hash + 1)..]);
            rest = rest[..hash];
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var q = rest.IndexOf('?');
        if (q >= 0)
        {
            query = ParseQuery(rest[(q + 1)..]);
            rest = rest[..q];
        }

        return new LinkParts(scheme, rest, query, name);
    }

    private static string Pct(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '%' && i + 2 < s.Length)
            {
                var hex = s.Substring(i + 1, 2);
                if (byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                {
                    sb.Append((char)value);
                    i += 2;
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> ParseQuery(string q)
    {
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(q))
        {
            return res;
        }

        foreach (var part in q.Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                res[Pct(part)] = "";
            }
            else
            {
                res[Pct(part[..eq])] = Pct(part[(eq + 1)..]);
            }
        }

        return res;
    }

    private static string Get(IReadOnlyDictionary<string, string> q, string key)
        => q.TryGetValue(key, out var v) ? v : "";

    private static string First(IReadOnlyDictionary<string, string> q, params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = Get(q, k);
            if (v.Length > 0)
            {
                return v;
            }
        }

        return "";
    }

    private static JsonArray ToJsonArray(string commaSeparated)
    {
        var arr = new JsonArray();
        foreach (var item in commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            arr.Add(item);
        }

        return arr;
    }

    private static bool IsTrue(IReadOnlyDictionary<string, string> q, params string[] keys)
    {
        foreach (var k in keys)
        {
            var v = Get(q, k);
            if (v is "1" or "true")
            {
                return true;
            }
        }

        return false;
    }

    private static bool SupportedNetwork(string net) => net is "" or "tcp" or "ws" or "grpc" or "http" or "h2"
        or "quic" or "httpupgrade";

    private static (string Host, int Port)? SplitHostPort(string s)
    {
        string host;
        string port;

        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            if (end < 0)
            {
                return null;
            }

            host = s[1..end];
            var rest = s[(end + 1)..];
            if (rest.StartsWith(':'))
            {
                rest = rest[1..];
            }

            port = rest;
        }
        else
        {
            var colon = s.LastIndexOf(':');
            if (colon < 0)
            {
                host = s;
                port = "";
            }
            else
            {
                host = s[..colon];
                port = s[(colon + 1)..];
            }
        }

        host = host.Trim();
        port = port.Trim();

        if (host.Length == 0 || !int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
            || p is < 1 or > 65535)
        {
            return null;
        }

        return (host, p);
    }

    private static JsonObject? BuildTls(IReadOnlyDictionary<string, string> q)
    {
        var sec = Get(q, "security");
        if (sec is not ("tls" or "reality" or "xtls"))
        {
            return null;
        }

        var tls = new JsonObject { ["enabled"] = true };

        var sni = First(q, "sni", "peer", "host");
        if (sni.Length > 0)
        {
            tls["server_name"] = sni;
        }

        var alpn = Get(q, "alpn");
        if (alpn.Length > 0)
        {
            tls["alpn"] = ToJsonArray(alpn);
        }

        var fp = Get(q, "fp");
        if (fp.Length > 0)
        {
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
        }

        if (IsTrue(q, "allowInsecure", "insecure", "allow_insecure"))
        {
            tls["insecure"] = true;
        }

        if (sec is "reality" || Get(q, "pbk").Length > 0)
        {
            if (fp.Length == 0)
            {
                tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = "chrome" };
            }

            tls["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = Get(q, "pbk"),
                ["short_id"] = Get(q, "sid"),
            };
        }

        return tls;
    }

    private static JsonObject? BuildTransport(IReadOnlyDictionary<string, string> q)
    {
        var net = First(q, "type", "net");
        var path = Get(q, "path");
        var host = Get(q, "host");

        switch (net)
        {
            case "ws":
            {
                var t = new JsonObject { ["type"] = "ws", ["path"] = path.Length > 0 ? path : "/" };
                if (host.Length > 0)
                {
                    t["headers"] = new JsonObject { ["Host"] = host };
                }

                return t;
            }

            case "grpc":
            {
                var service = First(q, "serviceName", "service_name");
                if (service.Length == 0)
                {
                    service = path;
                }

                return new JsonObject { ["type"] = "grpc", ["service_name"] = service };
            }

            case "http" or "h2":
            {
                var t = new JsonObject { ["type"] = "http", ["path"] = path.Length > 0 ? path : "/" };
                if (host.Length > 0)
                {
                    t["host"] = ToJsonArray(host);
                }

                return t;
            }

            case "quic":
                return new JsonObject { ["type"] = "quic" };

            case "httpupgrade":
                return new JsonObject
                {
                    ["type"] = "httpupgrade",
                    ["path"] = path.Length > 0 ? path : "/",
                    ["host"] = host,
                };

            default:
                return null;
        }
    }

    private static ServerNode? ParseVless(LinkParts l)
    {
        var at = l.Rest.LastIndexOf('@');
        if (at < 0)
        {
            return null;
        }

        var uuid = Pct(l.Rest[..at]);
        var hp = SplitHostPort(l.Rest[(at + 1)..]);
        if (hp is null || !IsUuid(uuid))
        {
            return null;
        }

        if (!SupportedNetwork(First(l.Query, "type", "net")))
        {
            return null;
        }

        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["server"] = hp.Value.Host,
            ["server_port"] = hp.Value.Port,
            ["uuid"] = uuid,
        };

        var flow = Get(l.Query, "flow");
        if (flow.Length > 0)
        {
            outbound["flow"] = flow;
        }

        var packetEncoding = First(l.Query, "packetEncoding", "packet_encoding");
        if (packetEncoding.Length > 0)
        {
            outbound["packet_encoding"] = packetEncoding;
        }

        var tls = BuildTls(l.Query);
        if (tls is not null)
        {
            outbound["tls"] = tls;
        }

        var transport = BuildTransport(l.Query);
        if (transport is not null)
        {
            outbound["transport"] = transport;
        }

        return Materialize(outbound, hp.Value.Host, hp.Value.Port, l.Name);
    }

    private static ServerNode? ParseVmess(LinkParts l)
    {
        var raw = Base64Url.TryDecodeOrNull(l.Rest);
        if (raw is null)
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (node is not JsonObject j)
        {
            return null;
        }

        var add = Str(j, "add");
        var port = Int(j, "port", -1);
        var vmessId = Str(j, "id");
        if (add.Length == 0 || port is < 1 or > 65535 || !IsUuid(vmessId))
        {
            return null;
        }

        var outbound = new JsonObject
        {
            ["type"] = "vmess",
            ["server"] = add,
            ["server_port"] = port,
            ["uuid"] = vmessId,
            ["security"] = First2(Str(j, "scy"), Str(j, "security"), "auto"),
            ["alter_id"] = Int(j, "aid", 0),
        };

        if (Str(j, "tls") == "tls")
        {
            var tls = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = First2(Str(j, "sni"), Str(j, "host"), add),
            };

            var alpn = Str(j, "alpn");
            if (alpn.Length > 0)
            {
                tls["alpn"] = ToJsonArray(alpn);
            }

            var fp = Str(j, "fp");
            if (fp.Length > 0)
            {
                tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
            }

            outbound["tls"] = tls;
        }

        var net = Str(j, "net");
        if (!SupportedNetwork(net))
        {
            return null;
        }

        var path = Str(j, "path");
        var host = Str(j, "host");

        switch (net)
        {
            case "ws":
            {
                var t = new JsonObject { ["type"] = "ws", ["path"] = path.Length > 0 ? path : "/" };
                if (host.Length > 0)
                {
                    t["headers"] = new JsonObject { ["Host"] = host };
                }

                outbound["transport"] = t;
                break;
            }

            case "grpc":
                outbound["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = path };
                break;

            case "h2" or "http":
            {
                var t = new JsonObject { ["type"] = "http", ["path"] = path.Length > 0 ? path : "/" };
                if (host.Length > 0)
                {
                    t["host"] = ToJsonArray(host);
                }

                outbound["transport"] = t;
                break;
            }

            case "quic":
                outbound["transport"] = new JsonObject { ["type"] = "quic" };
                break;

            case "httpupgrade":
                outbound["transport"] = new JsonObject
                {
                    ["type"] = "httpupgrade",
                    ["path"] = path.Length > 0 ? path : "/",
                    ["host"] = host,
                };
                break;
        }

        var name = Str(j, "ps");
        return Materialize(outbound, add, port, name.Length > 0 ? name : l.Name);
    }

    private static ServerNode? ParseTrojan(LinkParts l)
    {
        var at = l.Rest.LastIndexOf('@');
        if (at < 0)
        {
            return null;
        }

        var password = Pct(l.Rest[..at]);
        var hp = SplitHostPort(l.Rest[(at + 1)..]);
        if (hp is null)
        {
            return null;
        }

        if (!SupportedNetwork(First(l.Query, "type", "net")))
        {
            return null;
        }

        var q = l.Query;
        var outbound = new JsonObject
        {
            ["type"] = "trojan",
            ["server"] = hp.Value.Host,
            ["server_port"] = hp.Value.Port,
            ["password"] = password,
        };

        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = hp.Value.Host,
        };

        var sni = First(q, "sni", "peer");
        if (sni.Length > 0)
        {
            tls["server_name"] = sni;
        }

        var alpn = Get(q, "alpn");
        if (alpn.Length > 0)
        {
            tls["alpn"] = ToJsonArray(alpn);
        }

        var fp = Get(q, "fp");
        if (fp.Length > 0)
        {
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };
        }

        if (IsTrue(q, "allowInsecure", "insecure", "allow_insecure"))
        {
            tls["insecure"] = true;
        }

        outbound["tls"] = tls;

        var transport = BuildTransport(q);
        if (transport is not null)
        {
            outbound["transport"] = transport;
        }

        return Materialize(outbound, hp.Value.Host, hp.Value.Port, l.Name);
    }

    private static ServerNode? ParseShadowsocks(LinkParts l)
    {
        var rest = l.Rest;
        var q = l.Query;
        var method = "";
        var password = "";
        var host = "";
        var port = 0;

        var at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            var userInfo = rest[..at];
            var dec = Base64Url.TryDecodeOrNull(userInfo);

            if (dec is not null && dec.Contains(':'))
            {
                var c = dec.IndexOf(':');
                method = dec[..c];
                password = dec[(c + 1)..];
            }
            else
            {
                var ui = Pct(userInfo);
                var c = ui.IndexOf(':');
                if (c < 0)
                {
                    return null;
                }

                method = ui[..c];
                password = ui[(c + 1)..];
            }

            var hp = SplitHostPort(rest[(at + 1)..]);
            if (hp is null)
            {
                return null;
            }

            host = hp.Value.Host;
            port = hp.Value.Port;
        }
        else
        {
            var dec = Base64Url.TryDecodeOrNull(rest);
            if (dec is null)
            {
                return null;
            }

            var at2 = dec.LastIndexOf('@');
            if (at2 < 0)
            {
                return null;
            }

            var cred = dec[..at2];
            var c = cred.IndexOf(':');
            if (c < 0)
            {
                return null;
            }

            method = cred[..c];
            password = cred[(c + 1)..];

            var hp = SplitHostPort(dec[(at2 + 1)..]);
            if (hp is null)
            {
                return null;
            }

            host = hp.Value.Host;
            port = hp.Value.Port;
        }

        if (method.Length == 0)
        {
            return null;
        }

        var outbound = new JsonObject
        {
            ["type"] = "shadowsocks",
            ["server"] = host,
            ["server_port"] = port,
            ["method"] = method,
            ["password"] = password,
        };

        var plugin = Get(q, "plugin");
        if (plugin.Length > 0)
        {
            var semi = plugin.IndexOf(';');
            if (semi >= 0)
            {
                outbound["plugin"] = plugin[..semi];
                outbound["plugin_opts"] = plugin[(semi + 1)..];
            }
            else
            {
                outbound["plugin"] = plugin;
            }
        }

        return Materialize(outbound, host, port, l.Name);
    }

    private static ServerNode? ParseHysteria2(LinkParts l)
    {
        var at = l.Rest.LastIndexOf('@');
        if (at < 0)
        {
            return null;
        }

        var password = Pct(l.Rest[..at]);
        var hp = SplitHostPort(l.Rest[(at + 1)..]);
        if (hp is null)
        {
            return null;
        }

        var q = l.Query;
        var outbound = new JsonObject
        {
            ["type"] = "hysteria2",
            ["server"] = hp.Value.Host,
            ["server_port"] = hp.Value.Port,
            ["password"] = password,
        };

        var obfs = Get(q, "obfs");
        if (obfs.Length > 0)
        {
            var obfsObj = new JsonObject { ["type"] = obfs };
            var obfsPassword = Get(q, "obfs-password");
            if (obfsPassword.Length > 0)
            {
                obfsObj["password"] = obfsPassword;
            }

            outbound["obfs"] = obfsObj;
        }

        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = First2(Get(q, "sni"), "", hp.Value.Host),
        };

        var sni = Get(q, "sni");
        if (sni.Length > 0)
        {
            tls["server_name"] = sni;
        }

        var alpn = Get(q, "alpn");
        if (alpn.Length > 0)
        {
            tls["alpn"] = ToJsonArray(alpn);
        }

        if (IsTrue(q, "insecure", "allowInsecure", "allow_insecure"))
        {
            tls["insecure"] = true;
        }

        outbound["tls"] = tls;

        return Materialize(outbound, hp.Value.Host, hp.Value.Port, l.Name);
    }

    private static ServerNode? ParseTuic(LinkParts l)
    {
        var at = l.Rest.LastIndexOf('@');
        if (at < 0)
        {
            return null;
        }

        var cred = l.Rest[..at];
        var c = cred.IndexOf(':');
        string uuid;
        string pass;

        if (c >= 0)
        {
            uuid = Pct(cred[..c]);
            pass = Pct(cred[(c + 1)..]);
        }
        else
        {
            uuid = Pct(cred);
            pass = "";
        }

        var hp = SplitHostPort(l.Rest[(at + 1)..]);
        if (hp is null || !IsUuid(uuid))
        {
            return null;
        }

        var q = l.Query;
        var outbound = new JsonObject
        {
            ["type"] = "tuic",
            ["server"] = hp.Value.Host,
            ["server_port"] = hp.Value.Port,
            ["uuid"] = uuid,
            ["password"] = pass,
            ["congestion_control"] = First2(Get(q, "congestion_control"), "", "cubic"),
            ["udp_relay_mode"] = First2(Get(q, "udp_relay_mode"), "", "native"),
        };

        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = First2(Get(q, "sni"), "", hp.Value.Host),
        };

        var alpn = Get(q, "alpn");
        tls["alpn"] = alpn.Length > 0 ? ToJsonArray(alpn) : new JsonArray("h3");

        if (IsTrue(q, "allow_insecure", "insecure", "allowInsecure"))
        {
            tls["insecure"] = true;
        }

        outbound["tls"] = tls;

        return Materialize(outbound, hp.Value.Host, hp.Value.Port, l.Name);
    }

    private static ServerNode? ParseAnytls(LinkParts l)
    {
        var at = l.Rest.LastIndexOf('@');
        if (at < 0)
        {
            return null;
        }

        var password = Pct(l.Rest[..at]);
        var hp = SplitHostPort(l.Rest[(at + 1)..]);
        if (hp is null)
        {
            return null;
        }

        var q = l.Query;
        var outbound = new JsonObject
        {
            ["type"] = "anytls",
            ["server"] = hp.Value.Host,
            ["server_port"] = hp.Value.Port,
            ["password"] = password,
        };

        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = First2(Get(q, "sni"), "", hp.Value.Host),
        };

        var sni = Get(q, "sni");
        if (sni.Length > 0)
        {
            tls["server_name"] = sni;
        }

        if (IsTrue(q, "insecure", "allow_insecure", "allowInsecure"))
        {
            tls["insecure"] = true;
        }

        outbound["tls"] = tls;

        return Materialize(outbound, hp.Value.Host, hp.Value.Port, l.Name);
    }

    private static ServerNode Materialize(JsonObject outbound, string server, int port, string name)
    {
        outbound["tag"] ??= "";
        var node = new ServerNode
        {
            Tag = (string?)outbound["tag"] ?? "",
            Name = string.IsNullOrWhiteSpace(name) ? $"{server}:{port}" : name.Trim(),
            Type = (string?)outbound["type"] ?? "",
            Server = server,
            ServerPort = port,
            Outbound = outbound,
        };

        return node;
    }

    private static string Str(JsonObject j, string key)
    {
        var node = j[key];
        if (node is null)
        {
            return "";
        }

        try
        {
            return node.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.String => node.GetValue<string>() ?? "",
                System.Text.Json.JsonValueKind.Number => node.ToJsonString(),
                System.Text.Json.JsonValueKind.True => "true",
                _ => "",
            };
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static int Int(JsonObject j, string key, int fallback)
    {
        var raw = Str(j, key);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static string First2(string a, string b, string fallback) => a.Length > 0 ? a : (b.Length > 0 ? b : fallback);
}
