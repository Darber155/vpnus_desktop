using System.Text;

namespace VpnUs.Core.Apps;

/// <summary>Определение флага страны по имени сервера (как в Happ) без внешних баз GeoIP.</summary>
public static class FlagHelper
{
    private static readonly (string Needle, string Code)[] CountryHints =
    [
        ("netherlands", "NL"), ("amsterdam", "NL"), ("holland", "NL"),
        ("germany", "DE"), ("frankfurt", "DE"), ("berlin", "DE"),
        ("usa", "US"), ("united states", "US"), ("america", "US"), ("new york", "US"), ("los angeles", "US"), ("seattle", "US"), ("dallas", "US"), ("chicago", "US"), ("miami", "US"), ("ashburn", "US"),
        ("canada", "CA"), ("toronto", "CA"), ("montreal", "CA"),
        ("united kingdom", "GB"), ("london", "GB"), ("england", "GB"), ("britain", "GB"),
        ("france", "FR"), ("paris", "FR"), ("marseille", "FR"),
        ("sweden", "SE"), ("stockholm", "SE"),
        ("finland", "FI"), ("helsinki", "FI"),
        ("poland", "PL"), ("warsaw", "PL"),
        ("türkiye", "TR"), ("turkey", "TR"), ("istanbul", "TR"),
        ("russia", "RU"), ("moscow", "RU"), ("saint petersburg", "RU"),
        ("kazakhstan", "KZ"), ("almaty", "KZ"),
        ("armenia", "AM"), ("yerevan", "AM"),
        ("georgia", "GE"), ("tbilisi", "GE"),
        ("latvia", "LV"), ("riga", "LV"), ("lithuania", "LT"), ("vilnius", "LT"), ("estonia", "EE"), ("tallinn", "EE"),
        ("switzerland", "CH"), ("zurich", "CH"), ("geneva", "CH"),
        ("austria", "AT"), ("vienna", "AT"), ("belgium", "BE"), ("brussels", "BE"),
        ("spain", "ES"), ("madrid", "ES"), ("barcelona", "ES"),
        ("italy", "IT"), ("milan", "IT"), ("rome", "IT"),
        ("norway", "NO"), ("oslo", "NO"), ("denmark", "DK"), ("copenhagen", "DK"),
        ("czech", "CZ"), ("prague", "CZ"), ("hungary", "HU"), ("budapest", "HU"),
        ("romania", "RO"), ("bucharest", "RO"), ("bulgaria", "BG"), ("sofia", "BG"),
        ("ukraine", "UA"), ("kyiv", "UA"), ("kiev", "UA"),
        ("serbia", "RS"), ("belgrade", "RS"), ("moldova", "MD"), ("chisinau", "MD"),
        ("japan", "JP"), ("tokyo", "JP"), ("osaka", "JP"),
        ("singapore", "SG"), ("hong kong", "HK"), ("hongkong", "HK"),
        ("south korea", "KR"), ("seoul", "KR"), ("korea", "KR"),
        ("india", "IN"), ("mumbai", "IN"), ("china", "CN"), ("taiwan", "TW"),
        ("australia", "AU"), ("sydney", "AU"), ("melbourne", "AU"),
        ("brazil", "BR"), ("sao paulo", "BR"), ("argentina", "AR"), ("mexico", "MX"),
        ("israel", "IL"), ("tel aviv", "IL"), ("uae", "AE"), ("dubai", "AE"),
        ("south africa", "ZA"), ("egypt", "EG"), ("india", "IN"), ("indonesia", "ID"), ("malaysia", "MY"), ("thailand", "TH"), ("vietnam", "VN"),
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
