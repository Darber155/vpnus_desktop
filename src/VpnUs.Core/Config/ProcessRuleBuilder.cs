using System.Text;
using System.Text.Json.Nodes;

namespace VpnUs.Core.Config;

public sealed class ProcessRuleSet
{
    public List<string> Names { get; } = [];
    public List<string> Paths { get; } = [];
    public List<string> PathRegexes { get; } = [];

    public bool IsEmpty => Names.Count == 0 && Paths.Count == 0 && PathRegexes.Count == 0;

    public IEnumerable<JsonObject> ToRules(string outbound)
    {
        if (Names.Count > 0)
        {
            yield return new JsonObject
            {
                ["process_name"] = ToArray(Names),
                ["action"] = "route",
                ["outbound"] = outbound,
            };
        }

        if (Paths.Count > 0)
        {
            yield return new JsonObject
            {
                ["process_path"] = ToArray(Paths),
                ["action"] = "route",
                ["outbound"] = outbound,
            };
        }

        if (PathRegexes.Count > 0)
        {
            yield return new JsonObject
            {
                ["process_path_regex"] = ToArray(PathRegexes),
                ["action"] = "route",
                ["outbound"] = outbound,
            };
        }
    }

    public static JsonArray ToArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var v in values)
        {
            arr.Add(v);
        }

        return arr;
    }
}

/// <summary>
/// Построение правил route.rules для per-app split tunneling.
/// sing-box склеивает разные поля одного правила по AND, поэтому имя, полный путь
/// и regex разводятся в отдельные правила.
/// </summary>
public static class ProcessRuleBuilder
{
    public static ProcessRuleSet FromPaths(IEnumerable<string> exePaths, bool includeChildProcesses = true)
    {
        var set = new ProcessRuleSet();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var regexes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in exePaths)
        {
            var path = Normalize(raw);
            if (path.Length == 0)
            {
                continue;
            }

            var name = Path.GetFileName(path);
            if (name.Length > 0)
            {
                names.Add(name);
            }

            paths.Add(path);

            if (!includeChildProcesses)
            {
                continue;
            }

            // sing-box отдаёт Win32-путь процесса (1.9+), но регистр может отличаться
            // от того, что видит пользователь в ярлыке/реестре — поэтому regex с (?i).
            regexes.Add($"(?i)^{EscapeRegex(path)}$");

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && IsSafeDirectoryForRegex(dir))
            {
                regexes.Add($"(?i)^{EscapeRegex(dir)}\\\\.*$");
            }
        }

        set.Names.AddRange(names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        set.Paths.AddRange(paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        set.PathRegexes.AddRange(regexes.OrderBy(x => x, StringComparer.Ordinal));
        return set;
    }

    public static ProcessRuleSet FromNames(IEnumerable<string> processNames)
    {
        var set = new ProcessRuleSet();
        foreach (var name in processNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                set.Names.Add(name.Trim());
            }
        }

        return set;
    }

    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var p = path.Trim().Trim('"');
        if (p.Length == 0 || !p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        p = p.Replace('/', '\\');
        return p;
    }

    /// <summary>Экранирование спецсимволов Go RE2 без экранирования пробелов (\  в RE2 не нужен).</summary>
    public static string EscapeRegex(string literal)
    {
        var sb = new StringBuilder(literal.Length * 2);
        foreach (var c in literal)
        {
            if (c is '\\' or '.' or '+' or '*' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '^' or '$' or '|')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Каталог программы не должен совпадать с системными каталогами: regex на C:\Windows\System32
    /// отправил бы через VPN всё системное окружение.</summary>
    private static bool IsSafeDirectoryForRegex(string dir)
    {
        var normalized = dir.Replace('/', '\\').TrimEnd('\\');
        var root = Path.GetPathRoot(normalized)?.TrimEnd('\\') ?? "";

        if (string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lower = normalized.ToLowerInvariant();
        return !lower.Contains(@"\windows")
               && !lower.Contains(@"\program files\windows")
               && !lower.EndsWith(@"\program files", StringComparison.Ordinal)
               && !lower.EndsWith(@"\program files (x86)", StringComparison.Ordinal)
               && !lower.EndsWith(@"\programdata", StringComparison.Ordinal)
               && !lower.EndsWith(@"\users", StringComparison.Ordinal);
    }
}
