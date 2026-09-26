using System.Text;

namespace VpnUs.Core.Apps;

/// <summary>Определение флага страны по имени сервера (как в Happ) без внешних баз GeoIP.</summary>
public static class FlagHelper
{
    private static readonly (string Needle, string Code)[] CountryHints =
    [
        ("netherlands", "NL"), ("amsterdam", "NL"), ("holland", "NL"), ("нидерланды", "NL"), ("амстердам", "NL"), ("голландия", "NL"),
        ("germany", "DE"), ("frankfurt", "DE"), ("berlin", "DE"), ("германия", "DE"), ("франкфурт", "DE"), ("берлин", "DE"),
        ("usa", "US"), ("united states", "US"), ("america", "US"), ("new york", "US"), ("los angeles", "US"), ("seattle", "US"), ("dallas", "US"), ("chicago", "US"), ("miami", "US"), ("ashburn", "US"), ("сша", "US"), ("америка", "US"),
        ("canada", "CA"), ("toronto", "CA"), ("montreal", "CA"), ("канада", "CA"), ("торонто", "CA"),
        ("united kingdom", "GB"), ("london", "GB"), ("england", "GB"), ("britain", "GB"), ("великобритания", "GB"), ("лондон", "GB"), ("англия", "GB"),
        ("france", "FR"), ("paris", "FR"), ("marseille", "FR"), ("франция", "FR"), ("париж", "FR"),
        ("sweden", "SE"), ("stockholm", "SE"), ("швеция", "SE"), ("стокгольм", "SE"),
        ("finland", "FI"), ("helsinki", "FI"), ("финляндия", "FI"), ("хельсинки", "FI"),
        ("poland", "PL"), ("warsaw", "PL"), ("польша", "PL"), ("варшава", "PL"),
        ("türkiye", "TR"), ("turkey", "TR"), ("istanbul", "TR"), ("турция", "TR"), ("стамбул", "TR"),
        ("russia", "RU"), ("moscow", "RU"), ("saint petersburg", "RU"), ("россия", "RU"), ("москва", "RU"), ("санкт-петербург", "RU"),
        ("kazakhstan", "KZ"), ("almaty", "KZ"), ("казахстан", "KZ"), ("алматы", "KZ"), ("астана", "KZ"),
        ("armenia", "AM"), ("yerevan", "AM"), ("армения", "AM"), ("ереван", "AM"),
        ("georgia", "GE"), ("tbilisi", "GE"), ("грузия", "GE"), ("тбилиси", "GE"),
        ("latvia", "LV"), ("riga", "LV"), ("латвия", "LV"), ("рига", "LV"),
        ("lithuania", "LT"), ("vilnius", "LT"), ("литва", "LT"), ("вильнюс", "LT"),
        ("estonia", "EE"), ("tallinn", "EE"), ("эстония", "EE"), ("таллин", "EE"), ("таллинн", "EE"),
        ("switzerland", "CH"), ("zurich", "CH"), ("geneva", "CH"), ("швейцария", "CH"),
        ("austria", "AT"), ("vienna", "AT"), ("австрия", "AT"), ("вена", "AT"),
        ("belgium", "BE"), ("brussels", "BE"), ("бельгия", "BE"), ("брюссель", "BE"),
        ("spain", "ES"), ("madrid", "ES"), ("barcelona", "ES"), ("испания", "ES"), ("мадрид", "ES"),
        ("italy", "IT"), ("milan", "IT"), ("rome", "IT"), ("италия", "IT"), ("милан", "IT"), ("рим", "IT"),
        ("norway", "NO"), ("oslo", "NO"), ("норвегия", "NO"),
        ("denmark", "DK"), ("copenhagen", "DK"), ("дания", "DK"),
        ("czech", "CZ"), ("prague", "CZ"), ("чехия", "CZ"), ("прага", "CZ"),
        ("hungary", "HU"), ("budapest", "HU"), ("венгрия", "HU"),
        ("romania", "RO"), ("bucharest", "RO"), ("румыния", "RO"),
        ("bulgaria", "BG"), ("sofia", "BG"), ("болгария", "BG"),
        ("ukraine", "UA"), ("kyiv", "UA"), ("kiev", "UA"), ("украина", "UA"), ("киев", "UA"),
        ("serbia", "RS"), ("belgrade", "RS"), ("сербия", "RS"),
        ("moldova", "MD"), ("chisinau", "MD"), ("молдова", "MD"),
        ("japan", "JP"), ("tokyo", "JP"), ("osaka", "JP"), ("япония", "JP"), ("токио", "JP"),
        ("singapore", "SG"), ("сингапур", "SG"),
        ("hong kong", "HK"), ("hongkong", "HK"), ("гонконг", "HK"),
        ("south korea", "KR"), ("seoul", "KR"), ("korea", "KR"), ("корея", "KR"), ("сеул", "KR"),
        ("india", "IN"), ("mumbai", "IN"), ("индия", "IN"),
        ("china", "CN"), ("китай", "CN"),
        ("taiwan", "TW"), ("тайвань", "TW"),
        ("australia", "AU"), ("sydney", "AU"), ("melbourne", "AU"), ("австралия", "AU"),
        ("brazil", "BR"), ("sao paulo", "BR"), ("бразилия", "BR"),
        ("argentina", "AR"), ("аргентина", "AR"),
        ("mexico", "MX"), ("мексика", "MX"),
        ("israel", "IL"), ("tel aviv", "IL"), ("израиль", "IL"),
        ("uae", "AE"), ("dubai", "AE"), ("оаэ", "AE"), ("дубай", "AE"),
        ("south africa", "ZA"), ("egypt", "EG"), ("египет", "EG"),
        ("indonesia", "ID"), ("индонезия", "ID"),
        ("malaysia", "MY"), ("малайзия", "MY"),
        ("thailand", "TH"), ("таиланд", "TH"),
        ("vietnam", "VN"), ("вьетнам", "VN"),
    ];

    private static readonly HashSet<string> ValidCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AD", "AE", "AF", "AL", "AM", "AR", "AT", "AU", "AZ", "BA", "BD", "BE", "BG", "BH", "BR", "BY", "CA", "CH", "CL",
        "CN", "CO", "CR", "CY", "CZ", "DE", "DK", "DO", "DZ", "EC", "EE", "EG", "ES", "FI", "FR", "GB", "GE", "GR", "HK",
        "HR", "HU", "ID", "IE", "IL", "IN", "IQ", "IR", "IS", "IT", "JP", "KE", "KG", "KR", "KW", "KZ", "LT", "LU", "LV",
        "MD", "ME", "MK", "MN", "MT", "MX", "MY", "NG", "NL", "NO", "NP", "NZ", "OM", "PA", "PE", "PH", "PK", "PL", "PT",
        "QA", "RO", "RS", "RU", "SA", "SE", "SG", "SI", "SK", "TH", "TR", "TW", "UA", "UK", "US", "UZ", "VN", "ZA",
    };

    public static string Guess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var text = name.Trim();

        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '\uD83C' && text[i + 1] is >= '\uDDE6' and <= '\uDDFF')
            {
                // Региональный индикатор уже присутствует в имени — флаг можно не подставлять.
                return "";
            }
        }

        var normal = text.ToLowerInvariant();
        foreach (var (needle, code) in CountryHints)
        {
            if (normal.Contains(needle, StringComparison.Ordinal))
            {
                return ToFlag(code);
            }
        }

        foreach (var token in Tokenize(text))
        {
            if (token.Length == 2 && ValidCodes.Contains(token))
            {
                return ToFlag(token.ToUpperInvariant());
            }
        }

        return "";
    }

    public static string ToFlag(string twoLetterCode)
    {
        if (twoLetterCode.Length != 2)
        {
            return "";
        }

        var upper = twoLetterCode.ToUpperInvariant();
        if (upper[0] is < 'A' or > 'Z' || upper[1] is < 'A' or > 'Z')
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var c in upper)
        {
            sb.Append(char.ConvertFromUtf32(0x1F1E6 + (c - 'A')));
        }

        return sb.ToString();
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                sb.Append(c);
            }
            else
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }
}
