namespace VpnUs.Core.Models;

public sealed class AppSettings
{
    public string SubscriptionUrl { get; set; } = "";
    public int SubscriptionUpdateHours { get; set; } = 24;
    public bool SubscriptionAutoUpdate { get; set; } = true;

    public RoutingMode Mode { get; set; } = RoutingMode.BypassRu;
    public AppSplitMode AppSplit { get; set; } = AppSplitMode.OnlySelected;
    public string SelectedNode { get; set; } = "auto";

    public List<SelectedApp> SelectedApps { get; set; } = [];

    public bool BypassGames { get; set; } = true;
    public bool BlockAds { get; set; }
    public List<string> SelectiveRuleSets { get; set; } =
    [
        "youtube", "telegram", "instagram", "discord", "openai", "twitter",
    ];

    public string DnsDirect { get; set; } = "77.88.8.8";
    public string DnsProxy { get; set; } = "1.1.1.1";
    public string DnsStrategy { get; set; } = "ipv4_only";

    public TunStack TunStack { get; set; } = TunStack.Mixed;
    public string TunInterface { get; set; } = "happwrt0";
    public int TunMtu { get; set; } = 9000;
    public int ClashApiPort { get; set; } = 9090;
    public string LogLevel { get; set; } = "warn";
    public string UrlTestUrl { get; set; } = "http://cp.cloudflare.com/generate_204";
    public string UrlTestInterval { get; set; } = "3m";

    public List<string> CustomDirectDomains { get; set; } = [];
    public List<string> CustomProxyDomains { get; set; } = [];
    public List<string> CustomDirectCidrs { get; set; } = [];
    public List<string> CustomProxyCidrs { get; set; } = [];

    public bool AutoConnect { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool LaunchAtLogin { get; set; } = true;

    public string CoreVersion { get; set; } = "";
    public DateTimeOffset? SubscriptionUpdatedAt { get; set; }

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, Storage.JsonStore.Options);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, Storage.JsonStore.Options) ?? new AppSettings();
    }
}

public sealed class SelectedApp
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";

    public SelectedApp() { }

    public SelectedApp(string path, string name)
    {
        Path = path;
        Name = name;
    }

    public override string ToString() => $"{Name} ({Path})";
}
