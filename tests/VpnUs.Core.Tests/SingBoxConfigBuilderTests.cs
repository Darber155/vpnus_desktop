using System.Text.Json.Nodes;
using VpnUs.Core.Config;
using VpnUs.Core.Models;
using VpnUs.Core.Subscription;
using Xunit;

namespace VpnUs.Core.Tests;

public class SingBoxConfigBuilderTests
{
    private static (AppSettings Settings, List<ServerNode> Nodes) Fixture()
    {
        var nodes = ShareLinkParser.ParseSubscription(ShareLinkParserTests.SamplePlainBody()).Nodes;
        var settings = new AppSettings
        {
            Mode = RoutingMode.PerApp,
            AppSplit = AppSplitMode.OnlySelected,
            TunInterface = "happwrt0",
            TunStack = TunStack.Mixed,
            SelectedApps =
            [
                new SelectedApp(@"C:\Games\MyGame\game.exe", "My Game"),
                new SelectedApp(@"D:\Apps\Editor\editor.exe", "Editor"),
            ],
        };

        return (settings, nodes);
    }

    private static JsonObject Build(AppSettings settings, List<ServerNode> nodes, SingBoxBuildOptions? options = null)
        => SingBoxConfigBuilder.Build(settings, nodes, options ?? new SingBoxBuildOptions { CachePath = "cache.db" });

    private static JsonArray Rules(JsonObject config) => config["route"]!["rules"]!.AsArray();

    private static IEnumerable<JsonObject> RulesWithOutbound(JsonObject config, string outbound)
        => Rules(config).OfType<JsonObject>().Where(r => r["outbound"]?.GetValue<string>() == outbound);

    private static bool RuleSetContains(JsonObject config, string tag)
        => config["route"]!["rule_set"]?.AsArray().Any(rs => rs!["tag"]!.GetValue<string>() == tag) == true;

    [Fact]
    public void Build_HasTunInboundFindProcessAndOutbounds()
    {
        var (settings, nodes) = Fixture();
        var config = Build(settings, nodes);

        var tun = config["inbounds"]!.AsArray()[0]!.AsObject();
        Assert.Equal("tun", tun["type"]!.GetValue<string>());
        Assert.Equal("happwrt0", tun["interface_name"]!.GetValue<string>());
        Assert.Equal("mixed", tun["stack"]!.GetValue<string>());
        Assert.True(tun["auto_route"]!.GetValue<bool>());

        Assert.True(config["route"]!["find_process"]!.GetValue<bool>());
        Assert.Equal("dns-direct", config["route"]!["default_domain_resolver"]!.GetValue<string>());

        var tags = config["outbounds"]!.AsArray().Select(o => o!["tag"]!.GetValue<string>()).ToList();
        Assert.Contains("proxy", tags);
        Assert.Contains("auto", tags);
        Assert.Contains("direct", tags);
        Assert.Contains("block", tags);

        var selector = config["outbounds"]!.AsArray()[0]!.AsObject();
        Assert.Equal("selector", selector["type"]!.GetValue<string>());
        Assert.Equal("auto", selector["default"]!.GetValue<string>());
        Assert.Equal(nodes.Count + 1, selector["outbounds"]!.AsArray().Count);
    }

    [Fact]
    public void Build_PerAppOnlySelected_SelectedAppsProxyAndFinalDirect()
    {
        var (settings, nodes) = Fixture();
        var config = Build(settings, nodes);

        Assert.Equal("direct", config["route"]!["final"]!.GetValue<string>());

        var proxyRules = RulesWithOutbound(config, "proxy").ToList();
        var names = proxyRules.Where(r => r["process_name"] is not null)
            .SelectMany(r => r["process_name"]!.AsArray().Select(v => v!.GetValue<string>()))
            .ToList();
        var paths = proxyRules.Where(r => r["process_path"] is not null)
            .SelectMany(r => r["process_path"]!.AsArray().Select(v => v!.GetValue<string>()))
            .ToList();
        var regexes = proxyRules.Where(r => r["process_path_regex"] is not null)
            .SelectMany(r => r["process_path_regex"]!.AsArray().Select(v => v!.GetValue<string>()))
            .ToList();

        Assert.Contains("game.exe", names);
        Assert.Contains("editor.exe", names);
        Assert.Contains(@"C:\Games\MyGame\game.exe", paths);
        Assert.Contains(@"D:\Apps\Editor\editor.exe", paths);
        Assert.Contains(@"(?i)^C:\\Games\\MyGame\\.*$", regexes);

        Assert.Equal("dns-direct", config["dns"]!["final"]!.GetValue<string>());
    }

    [Fact]
    public void Build_PerAppAllExceptSelected_SelectedAppsDirectAndFinalProxy()
    {
        var (settings, nodes) = Fixture();
        settings.AppSplit = AppSplitMode.AllExceptSelected;

        var config = Build(settings, nodes);

        Assert.Equal("proxy", config["route"]!["final"]!.GetValue<string>());
        Assert.Equal("dns-proxy", config["dns"]!["final"]!.GetValue<string>());

        var directNames = RulesWithOutbound(config, "direct")
            .Where(r => r["process_name"] is not null)
            .SelectMany(r => r["process_name"]!.AsArray().Select(v => v!.GetValue<string>()))
            .ToList();

        Assert.Contains("game.exe", directNames);
    }

    [Fact]
    public void Build_PerAppAllThroughVpn_FinalProxy()
    {
        var (settings, nodes) = Fixture();
        settings.AppSplit = AppSplitMode.AllThroughVpn;

        var config = Build(settings, nodes);
        Assert.Equal("proxy", config["route"]!["final"]!.GetValue<string>());
    }

    [Fact]
    public void Build_AlwaysExcludesOwnProcessesAndWindowsUpdate()
    {
        var (settings, nodes) = Fixture();
        var config = Build(settings, nodes);

        var directNames = RulesWithOutbound(config, "direct")
            .Where(r => r["process_name"] is not null)
            .SelectMany(r => r["process_name"]!.AsArray().Select(v => v!.GetValue<string>()))
            .ToList();

        Assert.Contains("sing-box.exe", directNames);
        Assert.Contains("VpnUs.exe", directNames);
        Assert.Contains("TiWorker.exe", directNames);
    }

    [Fact]
    public void Build_BypassBlocked_UsesRefilterRuleSetsAndDirectFinal()
    {
        var (settings, nodes) = Fixture();
        settings.Mode = RoutingMode.BypassBlocked;

        var config = Build(settings, nodes);

        Assert.Equal("direct", config["route"]!["final"]!.GetValue<string>());
        Assert.True(RuleSetContains(config, RuleSetCatalog.TagRefilterDomains));
        Assert.True(RuleSetContains(config, RuleSetCatalog.TagRefilterIps));

        var refilter = Rules(config).OfType<JsonObject>()
            .First(r => r["rule_set"] is JsonArray arr && arr.Any(v => v!.GetValue<string>() == RuleSetCatalog.TagRefilterDomains));
        Assert.Equal("proxy", refilter["outbound"]!.GetValue<string>());

        var url = config["route"]!["rule_set"]!.AsArray()
            .First(rs => rs!["tag"]!.GetValue<string>() == RuleSetCatalog.TagRefilterDomains)!["url"]!.GetValue<string>();
        Assert.Contains("Re-filter-lists", url);
        Assert.Contains(".srs", url);
    }

    [Fact]
    public void Build_Selective_UsesConfiguredGeositeRuleSets()
    {
        var (settings, nodes) = Fixture();
        settings.Mode = RoutingMode.Selective;
        settings.SelectiveRuleSets = ["youtube", "telegram", "unknown-resource"];

        var config = Build(settings, nodes);

        Assert.Equal("direct", config["route"]!["final"]!.GetValue<string>());
        Assert.True(RuleSetContains(config, "geosite-youtube"));
        Assert.True(RuleSetContains(config, "geosite-telegram"));
        Assert.False(RuleSetContains(config, "geosite-unknown-resource"));

        var proxyRule = Rules(config).OfType<JsonObject>()
            .First(r => r["rule_set"] is JsonArray arr && arr.Any(v => v!.GetValue<string>() == "geosite-youtube"));
        Assert.Equal("proxy", proxyRule["outbound"]!.GetValue<string>());

        var downloadDetour = config["route"]!["rule_set"]!.AsArray()
            .First(rs => rs!["tag"]!.GetValue<string>() == "geosite-youtube")!["download_detour"]!.GetValue<string>();
        Assert.Equal("proxy", downloadDetour);
    }

    [Fact]
    public void Build_BypassRu_SendsRuDirectAndFinalProxy()
    {
        var (settings, nodes) = Fixture();
        settings.Mode = RoutingMode.BypassRu;

        var config = Build(settings, nodes);

        Assert.Equal("proxy", config["route"]!["final"]!.GetValue<string>());
        Assert.True(RuleSetContains(config, RuleSetCatalog.TagRuDomains));
        Assert.True(RuleSetContains(config, RuleSetCatalog.TagRuIps));

        var ruRule = Rules(config).OfType<JsonObject>()
            .First(r => r["rule_set"] is JsonArray arr && arr.Any(v => v!.GetValue<string>() == RuleSetCatalog.TagRuDomains));
        Assert.Equal("direct", ruRule["outbound"]!.GetValue<string>());
        Assert.Equal("dns-proxy", config["dns"]!["final"]!.GetValue<string>());

        var dnsRuRule = config["dns"]!["rules"]!.AsArray().OfType<JsonObject>()
            .First(r => r["rule_set"] is JsonArray arr && arr.Any(v => v!.GetValue<string>() == RuleSetCatalog.TagRuDomains));
        Assert.Equal("dns-direct", dnsRuRule["server"]!.GetValue<string>());
    }

    [Fact]
    public void Build_GamesDirect_WhenEnabled()
    {
        var (settings, nodes) = Fixture();
        settings.Mode = RoutingMode.Global;
        settings.BypassGames = true;

        var config = Build(settings, nodes);

        Assert.True(RuleSetContains(config, RuleSetCatalog.TagGames));

        var portRule = RulesWithOutbound(config, "direct")
            .First(r => r["port_range"] is JsonArray);

        var ports = portRule["port"]!.AsArray().Select(p => p!.ToJsonString().Trim('"')).ToList();
        var ranges = portRule["port_range"]!.AsArray().Select(p => p!.ToJsonString().Trim('"')).ToList();

        Assert.Contains("27015", ports);
        Assert.Contains("3074", ports);
        Assert.Contains("27000:27100", ranges);
        Assert.Contains("3478:3480", ranges);
        Assert.All(ports, p => Assert.DoesNotContain(':', p));
    }

    [Fact]
    public void Build_AdBlock_RejectsAds()
    {
        var (settings, nodes) = Fixture();
        settings.Mode = RoutingMode.Global;
        settings.BlockAds = true;

        var config = Build(settings, nodes);

        Assert.True(RuleSetContains(config, RuleSetCatalog.TagAds));
        Assert.Contains(Rules(config).OfType<JsonObject>(), r => r["action"]?.GetValue<string>() == "reject");
    }

    [Fact]
    public void Build_DnsServersAndHijack()
    {
        var (settings, nodes) = Fixture();
        settings.Mode = RoutingMode.Global;

        var config = Build(settings, nodes);

        var servers = config["dns"]!["servers"]!.AsArray();
        Assert.Contains(servers.OfType<JsonObject>(), s => s["tag"]!.GetValue<string>() == "dns-direct" && s["detour"] is null);
        Assert.Contains(servers.OfType<JsonObject>(), s => s["tag"]!.GetValue<string>() == "dns-proxy" && s["detour"]!.GetValue<string>() == "proxy");

        var firstRules = Rules(config).OfType<JsonObject>().Take(3).ToList();
        Assert.Equal("sniff", firstRules[0]["action"]!.GetValue<string>());
        Assert.Equal(53, firstRules[1]["port"]!.GetValue<int>());
        Assert.Equal("hijack-dns", firstRules[1]["action"]!.GetValue<string>());
        Assert.True(firstRules[2]["ip_is_private"]!.GetValue<bool>());
    }

    [Fact]
    public void Build_ClashApiConfigured()
    {
        var (settings, nodes) = Fixture();
        var config = Build(settings, nodes, new SingBoxBuildOptions { CachePath = "cache.db", ClashApiPort = 9191 });

        Assert.Equal("127.0.0.1:9191", config["experimental"]!["clash_api"]!["external_controller"]!.GetValue<string>());
        Assert.True(config["experimental"]!["cache_file"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Build_LocalRuleSetCache_SkipsMissingFilesAndUsesExisting()
    {
        var (settings, nodes) = Fixture();
        settings.Mode = RoutingMode.BypassBlocked;

        var dir = Path.Combine(Path.GetTempPath(), "vpnus-rs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            // Файлов нет → наборы не подключаем, но конфиг валиден и старт не упадёт.
            var empty = Build(settings, nodes, new SingBoxBuildOptions { RuleSetDirectory = dir });
            Assert.Null(empty["route"]!["rule_set"]);
            Assert.DoesNotContain(Rules(empty).OfType<JsonObject>(), r => r["rule_set"] is not null);

            // Появился только один файл из двух — подключается только он, правила ссылаются лишь на него.
            File.WriteAllBytes(Path.Combine(dir, RuleSetCatalog.FileName(RuleSetCatalog.TagRefilterDomains)), [0x01, 0x02, 0x03]);

            var partial = Build(settings, nodes, new SingBoxBuildOptions { RuleSetDirectory = dir });
            var ruleSets = partial["route"]!["rule_set"]!.AsArray();
            Assert.Single(ruleSets);
            Assert.Equal("local", ruleSets[0]!["type"]!.GetValue<string>());
            Assert.Equal("binary", ruleSets[0]!["format"]!.GetValue<string>());
            Assert.Equal(RuleSetCatalog.TagRefilterDomains, ruleSets[0]!["tag"]!.GetValue<string>());
            Assert.Contains(dir, ruleSets[0]!["path"]!.GetValue<string>());

            var proxyRule = Rules(partial).OfType<JsonObject>()
                .First(r => r["rule_set"] is JsonArray arr && arr.Any(v => v!.GetValue<string>() == RuleSetCatalog.TagRefilterDomains));
            var tags = proxyRule["rule_set"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
            Assert.DoesNotContain(RuleSetCatalog.TagRefilterIps, tags);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Build_LocalProxyInbound_AddsRuleToRouteThroughProxy()
    {
        var (settings, nodes) = Fixture();
        var config = Build(settings, nodes, new SingBoxBuildOptions { RuleSetDirectory = "x", LocalProxyPort = 2081 });

        var inbounds = config["inbounds"]!.AsArray();
        Assert.Contains(inbounds.OfType<JsonObject>(), i => i["tag"]?.GetValue<string>() == "local-in" && i["type"]?.GetValue<string>() == "mixed");

        var rule = Rules(config).OfType<JsonObject>()
            .First(r => r["inbound"] is JsonArray arr && arr.Any(v => v!.GetValue<string>() == "local-in"));
        Assert.Equal("proxy", rule["outbound"]!.GetValue<string>());
    }

    [Fact]
    public void Build_WithoutNodes_DoesNotCrashAndStaysDirect()
    {
        var (settings, _) = Fixture();
        var config = Build(settings, []);

        Assert.Equal("direct", config["route"]!["final"]!.GetValue<string>());
        Assert.Equal("dns-direct", config["dns"]!["final"]!.GetValue<string>());

        var tags = config["outbounds"]!.AsArray().Select(o => o!["tag"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("proxy", tags);
        Assert.Null(config["route"]!["rule_set"]);
    }

    [Fact]
    public void Build_JsonIsSerializableAndRounded()
    {
        var (settings, nodes) = Fixture();
        var json = SingBoxConfigBuilder.BuildJson(settings, nodes);

        var parsed = JsonNode.Parse(json)!.AsObject();
        Assert.NotNull(parsed["log"]);
        Assert.NotNull(parsed["dns"]);
        Assert.NotNull(parsed["inbounds"]);
        Assert.NotNull(parsed["outbounds"]);
        Assert.NotNull(parsed["route"]);
        Assert.NotNull(parsed["experimental"]);
    }

    [Fact]
    public void Build_SelectedNodePreservedOrDefaultsToAuto()
    {
        var (settings, nodes) = Fixture();
        settings.SelectedNode = nodes[2].Tag;

        var config = Build(settings, nodes);
        Assert.Equal(nodes[2].Tag, config["outbounds"]!.AsArray()[0]!["default"]!.GetValue<string>());

        settings.SelectedNode = "n-missing";
        var fallback = Build(settings, nodes);
        Assert.Equal("auto", fallback["outbounds"]!.AsArray()[0]!["default"]!.GetValue<string>());
    }

    [Fact]
    public void ProcessRuleBuilder_EscapesWindowsPaths()
    {
        var set = ProcessRuleBuilder.FromPaths([@"C:\Program Files\Foo App\bar.exe"]);

        Assert.Contains("bar.exe", set.Names);
        Assert.Contains(@"(?i)^C:\\Program Files\\Foo App\\.*$", set.PathRegexes);
    }

    [Fact]
    public void ProcessRuleBuilder_SkipsSystemDirectoriesForRegex()
    {
        var set = ProcessRuleBuilder.FromPaths([@"C:\Windows\System32\notepad.exe"]);

        // Точный путь добавляется всегда (регистронезависимо), а широкий regex по каталогу —
        // нет, иначе через VPN пошло бы всё содержимое System32.
        Assert.Single(set.PathRegexes);
        Assert.EndsWith(@"notepad\.exe$", set.PathRegexes[0]);
        Assert.DoesNotContain(set.PathRegexes, r => r.EndsWith(@"\\.*$"));
        Assert.Contains("notepad.exe", set.Names);
    }

    [Fact]
    public void ProcessRuleBuilder_IgnoresNonExePaths()
    {
        var set = ProcessRuleBuilder.FromPaths(["", @"C:\temp\readme.txt", "\"C:\\app\\a.exe\""]);

        Assert.Equal(["a.exe"], set.Names);
        Assert.Single(set.Paths);
    }
}
