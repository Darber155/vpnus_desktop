namespace VpnUs.Core.Models;

public enum RoutingMode
{
    BypassBlocked,
    Selective,
    BypassRu,
    Global,
    PerApp,
}

public enum AppSplitMode
{
    AllThroughVpn,
    OnlySelected,
    AllExceptSelected,
}

public enum TunStack
{
    Gvisor,
    System,
    Mixed,
}

public enum ServiceState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error,
}

public static class RoutingModeInfo
{
    public static readonly string[] SelectiveRuleSets =
    [
        "youtube", "google", "instagram", "facebook", "twitter", "tiktok", "telegram",
        "discord", "openai", "netflix", "spotify", "twitch", "reddit", "github",
        "microsoft", "apple", "steam", "category-games",
    ];

    public static readonly IReadOnlyDictionary<string, string> SelectiveRuleSetTitles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["youtube"] = "YouTube",
            ["google"] = "Google",
            ["instagram"] = "Instagram",
            ["facebook"] = "Facebook",
            ["twitter"] = "X / Twitter",
            ["tiktok"] = "TikTok",
            ["telegram"] = "Telegram",
            ["discord"] = "Discord",
            ["openai"] = "OpenAI / ChatGPT",
            ["netflix"] = "Netflix",
            ["spotify"] = "Spotify",
            ["twitch"] = "Twitch",
            ["reddit"] = "Reddit",
            ["github"] = "GitHub",
            ["microsoft"] = "Microsoft",
            ["apple"] = "Apple",
            ["steam"] = "Steam",
            ["category-games"] = "Игры",
        };

    public static string DisplayName(RoutingMode mode) => mode switch
    {
        RoutingMode.BypassBlocked => "Только заблокированное (RU-обход)",
        RoutingMode.Selective => "Только выбранные ресурсы",
        RoutingMode.BypassRu => "Всё через VPN, кроме РФ",
        RoutingMode.Global => "Global (всё через VPN)",
        RoutingMode.PerApp => "Только выбранные приложения",
        _ => mode.ToString(),
    };

    public static string DisplayName(AppSplitMode mode) => mode switch
    {
        AppSplitMode.AllThroughVpn => "Все приложения через VPN",
        AppSplitMode.OnlySelected => "Только выбранные через VPN",
        AppSplitMode.AllExceptSelected => "Все, кроме выбранных",
        _ => mode.ToString(),
    };
}
