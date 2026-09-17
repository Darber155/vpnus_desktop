using System.Text;

namespace VpnUs.Core.Text;

public static class Base64Url
{
    public static bool TryDecode(string? input, out string text)
    {
        text = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c is ' ' or '\r' or '\n' or '\t')
            {
                continue;
            }

            sb.Append(c switch
            {
                '-' => '+',
                '_' => '/',
                _ => c,
            });
        }

        var normalized = sb.ToString();
        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
        }

        try
        {
            var bytes = Convert.FromBase64String(normalized);
            text = new UTF8Encoding(false).GetString(bytes);
            return text.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string? TryDecodeOrNull(string? input) => TryDecode(input, out var text) ? text : null;

    public static bool LooksLikeBase64(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        foreach (var c in input)
        {
            if (char.IsLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_' or '\r' or '\n' or ' ')
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
