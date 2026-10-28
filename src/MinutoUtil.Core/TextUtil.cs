using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MinutoUtil.Core;

public static partial class TextUtil
{
    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Lowercase ASCII letters, digits and dashes, for file names.</summary>
    public static string Slug(string text, int maxLength = 40)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue; // accents: "é" -> "e"
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > maxLength) slug = slug[..maxLength].TrimEnd('-');
        return slug.Length == 0 ? "item" : slug;
    }

    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = ScriptOrStyle().Replace(html, " ");
        text = Tag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        return Whitespace().Replace(text, " ").Trim();
    }

    public static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength];

    /// <summary>A year as the owner reads it: negative years are before Christ ("490 a.C."), 0 or null is unknown ("?").</summary>
    public static string YearLabel(int? year) => year switch
    {
        null or 0 => "?",
        < 0 => $"{-year.Value} a.C.",
        _ => year.Value.ToString(CultureInfo.InvariantCulture),
    };

    public static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Lowercase letters and digits with accents removed and single spaces, for loose text comparison.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return Whitespace().Replace(sb.ToString(), " ").Trim();
    }
}
