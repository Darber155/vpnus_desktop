namespace VpnUs.Core.Config;

/// <summary>
/// Источники rule-set'ов sing-box. Кэширование выполняет сам sing-box через
/// experimental.cache_file (options.CachePath), скачивание идёт через outbound "proxy".
/// </summary>
public static class RuleSetCatalog
{
    public const string RefilterDomainsUrl =
        "https://github.com/1andrevich/Re-filter-lists/releases/latest/download/ruleset-domain-refilter_domains.srs";

    public const string RefilterIpsUrl =
        "https://github.com/1andrevich/Re-filter-lists/releases/latest/download/ruleset-ip-refilter_ipsum.srs";

    public const string TagRefilterDomains = "refilter-domains";
    public const string TagRefilterIps = "refilter-ips";
    public const string TagAds = "geosite-ads";
    public const string TagRuDomains = "geosite-ru";
    public const string TagRuIps = "geoip-ru";
    public const string TagGames = "geosite-category-games";

    public static string GeositeUrl(string name) =>
        $"https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/geosite-{name}.srs";

    public static string GeoipUrl(string name) =>
        $"https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/geoip-{name}.srs";

    public static string GeositeTag(string name) => $"geosite-{name}";

    public static string GeoipTag(string name) => $"geoip-{name}";
}
