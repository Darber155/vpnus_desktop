using System.Text.Json;
using System.Text.Json.Nodes;
using VpnUs.Core.Models;

namespace VpnUs.Core.Config;

public sealed record SingBoxBuildOptions
{
    public int ClashApiPort { get; init; } = 9090;
    public string CachePath { get; init; } = "cache.db";
    public bool EnableClashApi { get; init; } = true;
    public string LogLevel { get; init; } = "warn";
}

/// <summary>
/// Генератор конфига sing-box. C#-порт HappWrt gen.uc + режим per-app split tunneling.
/// Формат: sing-box 1.11+ (маршрутные actions, DNS-серверы нового формата,
/// route.default_domain_resolver). Поле tun.stack оставлено по требованию ТЗ,
/// для sing-box 1.16+ служба выставляет ENABLE_DEPRECATED_TUN_STACK=true.
/// </summary>
public static class SingBoxConfigBuilder
{
    public static readonly string[] OwnProcessNames =
    [
        "sing-box.exe", "VpnUs.exe", "VpnUs.Service.exe", "VpnUs.App.exe",
    ];

    public static readonly string[] WindowsUpdateProcessNames =
    [
        "MoUsoCoreWorker.exe", "TiWorker.exe", "TrustedInstaller.exe", "UsoClient.exe",
        "wusa.exe", "SetupHost.exe", "WaaSMedic.exe", "WindowsUpdateBox.exe",
    ];

    public static readonly int[] GamePorts = [27015, 27036, 3074, 3544, 4500];

    public static readonly string[] GamePortRanges = ["27000:27100", "3478:3480", "9295:9304", "5000:5500"];

    private static readonly string[] TunExcludeAddresses =
    [
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16",
        "127.0.0.0/8", "::1/128", "fc00::/7", "fe80::/10",
    ];

    public static JsonObject Build(AppSettings settings, IReadOnlyList<ServerNode> nodes, SingBoxBuildOptions? options = null)
    {
        options ??= new SingBoxBuildOptions
        {
            ClashApiPort = settings.ClashApiPort,
            LogLevel = settings.LogLevel,
        };

        var nodesList = nodes ?? [];
        var hasProxy = nodesList.Count > 0;

        var selective = settings.SelectiveRuleSets
            .Where(RoutingModeInfo.SelectiveRuleSets.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gamesEnabled = settings.BypassGames || selective.Contains("category-games", StringComparer.OrdinalIgnoreCase);
        var adsEnabled = settings.BlockAds && hasProxy;

        var ruleSetJson = new JsonArray();
        var ruleSetTags = new HashSet<string>(StringComparer.Ordinal);

        void AddRuleSet(string tag, string url)
        {
            if (!hasProxy || !ruleSetTags.Add(tag))
            {
                return;
            }

            ruleSetJson.Add(new JsonObject
            {
                ["type"] = "remote",
                ["tag"] = tag,
                ["url"] = url,
                ["download_detour"] = "proxy",
                ["update_interval"] = "7d",
            });
        }

        if (gamesEnabled)
        {
            AddRuleSet(RuleSetCatalog.TagGames, RuleSetCatalog.GeositeUrl("category-games"));
        }

        switch (settings.Mode)
        {
            case RoutingMode.BypassBlocked:
                AddRuleSet(RuleSetCatalog.TagRefilterDomains, RuleSetCatalog.RefilterDomainsUrl);
                AddRuleSet(RuleSetCatalog.TagRefilterIps, RuleSetCatalog.RefilterIpsUrl);
                break;
            case RoutingMode.Selective:
                foreach (var name in selective)
                {
                    AddRuleSet(RuleSetCatalog.GeositeTag(name), RuleSetCatalog.GeositeUrl(name));
                }

                break;
            case RoutingMode.BypassRu:
                AddRuleSet(RuleSetCatalog.TagRuDomains, RuleSetCatalog.GeositeUrl("category-ru"));
                AddRuleSet(RuleSetCatalog.TagRuIps, RuleSetCatalog.GeoipUrl("ru"));
                break;
        }

        if (adsEnabled)
        {
            AddRuleSet(RuleSetCatalog.TagAds, RuleSetCatalog.GeositeUrl("category-ads-all"));
        }

        var rules = new JsonArray();
        void AddRule(JsonObject rule) => rules.Add(rule);

        void AddRuleSetDirect(params string[] tags)
        {
            var present = tags.Where(ruleSetTags.Contains).ToArray();
            if (present.Length > 0)
            {
                AddRule(new JsonObject { ["rule_set"] = StringArray(present), ["action"] = "route", ["outbound"] = "direct" });
            }
        }

        void AddRuleSetProxy(params string[] tags)
        {
            var present = tags.Where(ruleSetTags.Contains).ToArray();
            if (present.Length > 0)
            {
                AddRule(new JsonObject { ["rule_set"] = StringArray(present), ["action"] = "route", ["outbound"] = "proxy" });
            }
        }

        void AddProcessRules(ProcessRuleSet set, string outbound)
        {
            if (set.IsEmpty)
            {
                return;
            }

            foreach (var rule in set.ToRules(outbound))
            {
                AddRule(rule);
            }
        }

        // База: sniff для доменов, перехват DNS, локальные сети напрямую.
        AddRule(new JsonObject { ["action"] = "sniff" });
        AddRule(new JsonObject { ["port"] = 53, ["action"] = "hijack-dns" });
        AddRule(new JsonObject { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "direct" });

        // Собственные процессы и системные обновления никогда не заворачиваем в VPN.
        AddProcessRules(ProcessRuleBuilder.FromNames(OwnProcessNames), "direct");
        AddProcessRules(ProcessRuleBuilder.FromNames(WindowsUpdateProcessNames), "direct");

        if (gamesEnabled)
        {
            AddRuleSetDirect(RuleSetCatalog.TagGames);

            // В sing-box "port" принимает только числа, "port_range" — только строки-диапазоны.
            AddRule(new JsonObject
            {
                ["port"] = IntArray(GamePorts),
                ["port_range"] = StringArray(GamePortRanges),
                ["action"] = "route",
                ["outbound"] = "direct",
            });
        }

        var selectedApps = ProcessRuleBuilder.FromPaths(settings.SelectedApps.Select(a => a.Path));
        string finalOutbound;

        switch (settings.Mode)
        {
            case RoutingMode.PerApp:
                switch (settings.AppSplit)
                {
                    case AppSplitMode.OnlySelected:
                        AddProcessRules(selectedApps, "proxy");
                        finalOutbound = "direct";
                        break;
                    case AppSplitMode.AllExceptSelected:
                        AddProcessRules(selectedApps, "direct");
                        finalOutbound = "proxy";
                        break;
                    default:
                        finalOutbound = "proxy";
                        break;
                }

                break;

            case RoutingMode.BypassBlocked:
                AddCustomDirect(settings, AddRule);
                AddAdsBlock(adsEnabled, AddRule);
                AddRuleSetProxy(RuleSetCatalog.TagRefilterDomains, RuleSetCatalog.TagRefilterIps);
                AddCustomProxy(settings, hasProxy, AddRule);
                finalOutbound = "direct";
                break;

            case RoutingMode.Selective:
                AddCustomDirect(settings, AddRule);
                AddAdsBlock(adsEnabled, AddRule);
                AddRuleSetProxy(selective.Select(RuleSetCatalog.GeositeTag).ToArray());
                AddCustomProxy(settings, hasProxy, AddRule);
                finalOutbound = "direct";
                break;

            case RoutingMode.BypassRu:
                AddCustomDirect(settings, AddRule);
                AddAdsBlock(adsEnabled, AddRule);
                AddRuleSetDirect(RuleSetCatalog.TagRuDomains, RuleSetCatalog.TagRuIps);
                AddCustomProxy(settings, hasProxy, AddRule);
                finalOutbound = "proxy";
                break;

            default:
                AddCustomDirect(settings, AddRule);
                AddAdsBlock(adsEnabled, AddRule);
                AddCustomProxy(settings, hasProxy, AddRule);
                finalOutbound = "proxy";
                break;
        }

        var dns = BuildDns(settings, options, hasProxy, finalOutbound, ruleSetTags, selective, selectedApps);

        var route = new JsonObject
        {
            ["rules"] = rules,
            ["final"] = finalOutbound,
            ["auto_detect_interface"] = true,
            ["find_process"] = true,
            ["default_domain_resolver"] = "dns-direct",
        };

        if (ruleSetJson.Count > 0)
        {
            route["rule_set"] = ruleSetJson;
        }

        var tun = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = "tun-in",
            ["interface_name"] = settings.TunInterface,
            ["address"] = StringArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"),
            ["mtu"] = settings.TunMtu,
            ["auto_route"] = true,
            ["strict_route"] = false,
            ["stack"] = settings.TunStack.ToString().ToLowerInvariant(),
            ["endpoint_independent_nat"] = true,
            ["udp_timeout"] = "5m",
            ["route_exclude_address"] = StringArray(TunExcludeAddresses),
        };

        var experimental = new JsonObject
        {
            ["cache_file"] = new JsonObject
            {
                ["enabled"] = true,
                ["path"] = options.CachePath,
            },
        };

        if (options.EnableClashApi)
        {
            experimental["clash_api"] = new JsonObject
            {
                ["external_controller"] = $"127.0.0.1:{options.ClashApiPort}",
                ["default_mode"] = "rule",
            };
        }

        var config = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = options.LogLevel,
                ["timestamp"] = true,
            },
            ["dns"] = dns,
            ["inbounds"] = new JsonArray(tun),
            ["outbounds"] = BuildOutbounds(settings, nodesList),
            ["route"] = route,
            ["experimental"] = experimental,
        };

        return config;
    }

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    public static string BuildJson(AppSettings settings, IReadOnlyList<ServerNode> nodes, SingBoxBuildOptions? options = null)
        => Build(settings, nodes, options).ToJsonString(IndentedJson);

    private static JsonArray BuildOutbounds(AppSettings settings, IReadOnlyList<ServerNode> nodes)
    {
        var outbounds = new JsonArray();

        if (nodes.Count > 0)
        {
            var tags = nodes.Select(n => n.Tag).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var defaultTag = tags.Contains(settings.SelectedNode, StringComparer.Ordinal) ? settings.SelectedNode : "auto";

            var selectorList = new JsonArray { "auto" };
            foreach (var tag in tags)
            {
                selectorList.Add(tag);
            }

            outbounds.Add(new JsonObject
            {
                ["type"] = "selector",
                ["tag"] = "proxy",
                ["outbounds"] = selectorList,
                ["default"] = defaultTag,
                ["interrupt_exist_connections"] = true,
            });

            var autoList = new JsonArray();
            foreach (var tag in tags)
            {
                autoList.Add(tag);
            }

            outbounds.Add(new JsonObject
            {
                ["type"] = "urltest",
                ["tag"] = "auto",
                ["outbounds"] = autoList,
                ["url"] = settings.UrlTestUrl,
                ["interval"] = settings.UrlTestInterval,
                ["tolerance"] = 50,
                ["idle_timeout"] = "30m",
                ["interrupt_exist_connections"] = true,
            });

            foreach (var node in nodes)
            {
                outbounds.Add(node.Outbound.DeepClone());
            }
        }

        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        outbounds.Add(new JsonObject { ["type"] = "block", ["tag"] = "block" });
        return outbounds;
    }

    private static JsonObject BuildDns(
        AppSettings settings,
        SingBoxBuildOptions options,
        bool hasProxy,
        string finalOutbound,
        HashSet<string> ruleSetTags,
        List<string> selective,
        ProcessRuleSet selectedApps)
    {
        var servers = new JsonArray
        {
            // detour у прямого сервера не указываем: sing-box 1.14 отвергает
            // "detour to an empty direct outbound makes no sense" при старте.
            new JsonObject
            {
                ["type"] = "udp",
                ["tag"] = "dns-direct",
                ["server"] = settings.DnsDirect,
            },
        };

        if (hasProxy)
        {
            servers.Add(new JsonObject
            {
                ["type"] = "udp",
                ["tag"] = "dns-proxy",
                ["server"] = settings.DnsProxy,
                ["detour"] = "proxy",
            });
        }

        var dnsRules = new JsonArray();

        void AddDnsRule(JsonObject rule) => dnsRules.Add(rule);

        void AddDnsRuleSet(IEnumerable<string> tags, string server)
        {
            var present = tags.Where(ruleSetTags.Contains).ToArray();
            if (present.Length > 0)
            {
                AddDnsRule(new JsonObject { ["rule_set"] = StringArray(present), ["server"] = server });
            }
        }

        void AddDnsProcessRules(ProcessRuleSet set, string server)
        {
            if (set.Names.Count > 0)
            {
                AddDnsRule(new JsonObject { ["process_name"] = ProcessRuleSet.ToArray(set.Names), ["server"] = server });
            }

            if (set.Paths.Count > 0)
            {
                AddDnsRule(new JsonObject { ["process_path"] = ProcessRuleSet.ToArray(set.Paths), ["server"] = server });
            }

            if (set.PathRegexes.Count > 0)
            {
                AddDnsRule(new JsonObject { ["process_path_regex"] = ProcessRuleSet.ToArray(set.PathRegexes), ["server"] = server });
            }
        }

        if (settings.CustomDirectDomains.Count > 0)
        {
            AddDnsRule(new JsonObject { ["domain_suffix"] = StringArray(settings.CustomDirectDomains), ["server"] = "dns-direct" });
        }

        if (hasProxy)
        {
            switch (settings.Mode)
            {
                case RoutingMode.BypassRu:
                    AddDnsRuleSet([RuleSetCatalog.TagRuDomains], "dns-direct");
                    break;
                case RoutingMode.BypassBlocked:
                    AddDnsRuleSet([RuleSetCatalog.TagRefilterDomains], "dns-proxy");
                    break;
                case RoutingMode.Selective:
                    AddDnsRuleSet(selective.Select(RuleSetCatalog.GeositeTag), "dns-proxy");
                    break;
                case RoutingMode.PerApp:
                    if (settings.AppSplit == AppSplitMode.OnlySelected)
                    {
                        AddDnsProcessRules(selectedApps, "dns-proxy");
                    }
                    else if (settings.AppSplit == AppSplitMode.AllExceptSelected)
                    {
                        AddDnsProcessRules(selectedApps, "dns-direct");
                    }

                    break;
            }

            if (options.EnableClashApi)
            {
                AddDnsRule(new JsonObject { ["clash_mode"] = "direct", ["server"] = "dns-direct" });
                AddDnsRule(new JsonObject { ["clash_mode"] = "global", ["server"] = "dns-proxy" });
            }

            if (settings.CustomProxyDomains.Count > 0)
            {
                AddDnsRule(new JsonObject { ["domain_suffix"] = StringArray(settings.CustomProxyDomains), ["server"] = "dns-proxy" });
            }
        }

        var dnsFinal = hasProxy && finalOutbound == "proxy" ? "dns-proxy" : "dns-direct";

        return new JsonObject
        {
            ["servers"] = servers,
            ["rules"] = dnsRules,
            ["final"] = dnsFinal,
            ["strategy"] = settings.DnsStrategy,
        };
    }

    private static void AddCustomDirect(AppSettings settings, Action<JsonObject> add)
    {
        if (settings.CustomDirectCidrs.Count > 0)
        {
            add(new JsonObject { ["ip_cidr"] = StringArray(settings.CustomDirectCidrs), ["action"] = "route", ["outbound"] = "direct" });
        }

        if (settings.CustomDirectDomains.Count > 0)
        {
            add(new JsonObject { ["domain_suffix"] = StringArray(settings.CustomDirectDomains), ["action"] = "route", ["outbound"] = "direct" });
        }
    }

    private static void AddCustomProxy(AppSettings settings, bool hasProxy, Action<JsonObject> add)
    {
        if (!hasProxy)
        {
            return;
        }

        if (settings.CustomProxyCidrs.Count > 0)
        {
            add(new JsonObject { ["ip_cidr"] = StringArray(settings.CustomProxyCidrs), ["action"] = "route", ["outbound"] = "proxy" });
        }

        if (settings.CustomProxyDomains.Count > 0)
        {
            add(new JsonObject { ["domain_suffix"] = StringArray(settings.CustomProxyDomains), ["action"] = "route", ["outbound"] = "proxy" });
        }
    }

    private static void AddAdsBlock(bool adsEnabled, Action<JsonObject> add)
    {
        if (adsEnabled)
        {
            add(new JsonObject { ["rule_set"] = StringArray(RuleSetCatalog.TagAds), ["action"] = "reject" });
        }
    }

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values)
        {
            arr.Add(v);
        }

        return arr;
    }

    private static JsonArray StringArray(params string[] values) => StringArray((IEnumerable<string>)values);

    private static JsonArray IntArray(IEnumerable<int> values)
    {
        var arr = new JsonArray();
        foreach (var v in values)
        {
            arr.Add(v);
        }

        return arr;
    }
}
