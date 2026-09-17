using VpnUs.Core.Models;

namespace VpnUs.Core.Config;

/// <summary>
/// Каталог rule-set'ов: какие наборы нужны режиму и как они называются на диске.
/// Служба скачивает .srs в кэш (ProgramData\VpnUs\rulesets) и ссылается на локальные файлы —
/// тогда sing-box не падает, если GitHub/прокси недоступны при старте.
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

    public static string FileName(string tag) => tag + ".srs";

    /// <summary>Наборы, необходимые текущим настройкам (тег → URL).</summary>
    public static List<(string Tag, string Url)> RequiredFor(AppSettings settings)
    {
        var result = new List<(string Tag, string Url)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string tag, string url)
        {
            if (seen.Add(tag))
            {
                result.Add((tag, url));
            }
        }

        var selective = settings.SelectiveRuleSets
            .Where(RoutingModeInfo.SelectiveRuleSets.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (settings.BypassGames || selective.Contains("category-games", StringComparer.OrdinalIgnoreCase))
        {
            Add(TagGames, GeositeUrl("category-games"));
        }

        switch (settings.Mode)
        {
            case RoutingMode.BypassBlocked:
                Add(TagRefilterDomains, RefilterDomainsUrl);
                Add(TagRefilterIps, RefilterIpsUrl);
                break;

            case RoutingMode.Selective:
                foreach (var name in selective)
                {
                    Add(GeositeTag(name), GeositeUrl(name));
                }

                break;

            case RoutingMode.BypassRu:
                Add(TagRuDomains, GeositeUrl("category-ru"));
                Add(TagRuIps, GeoipUrl("ru"));
                break;
        }

        if (settings.BlockAds)
        {
            Add(TagAds, GeositeUrl("category-ads-all"));
        }

        return result;
    }
}
