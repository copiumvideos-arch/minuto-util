using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MinutoUtil.Core.Scripts;

/// <summary>
/// Makes script text safe to read aloud and to show: no em or en dashes (the channel never uses them), and numbers
/// spelled out in Brazilian Portuguese for the voice, since a TTS can read "1944" as "um nove quatro quatro".
/// </summary>
public static partial class SpokenText
{
    [GeneratedRegex(@"\s*[\u2014\u2013\u2012\u2015]\s*")]
    private static partial Regex Dash();

    [GeneratedRegex(@"\s+,")]
    private static partial Regex SpaceBeforeComma();

    [GeneratedRegex(@",(\s*,)+")]
    private static partial Regex RepeatedComma();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    /// <summary>"24.000", "1944", "2,5", "40%": the number forms a Portuguese script uses.</summary>
    [GeneratedRegex(@"(?<!\w)(\d{1,3}(?:\.\d{3})+|\d+)(?:,(\d+))?(\s?%)?(?!\w)")]
    private static partial Regex Number();

    /// <summary>Replaces every em or en dash with a comma, the way a person reading it aloud would pause.</summary>
    public static string WithoutDashes(string text)
    {
        var result = Dash().Replace(text, ", ");
        result = RepeatedComma().Replace(result, ",");
        result = SpaceBeforeComma().Replace(result, ",");
        result = result.Replace(",.", ".").Replace(", .", ".");
        return Spaces().Replace(result, " ").Trim().TrimStart(',').Trim();
    }

    public static bool HasDigits(string text) => text.Any(char.IsAsciiDigit);

    /// <summary>The text with each number written out: "Em 1944, 24.000 soldados" becomes "Em mil novecentos e quarenta e quatro, vinte e quatro mil soldados".</summary>
    public static string SpellNumbers(string text) =>
        Number().Replace(text, m =>
        {
            var whole = long.Parse(m.Groups[1].Value.Replace(".", ""), CultureInfo.InvariantCulture);
            var words = new StringBuilder(Portuguese(whole));
            if (m.Groups[2].Success)
            {
                words.Append(" vírgula ");
                // "2,05" is "dois vírgula zero cinco": leading zeros are read one by one.
                var decimals = m.Groups[2].Value;
                var zeros = decimals.TakeWhile(c => c == '0').Count();
                words.Append(string.Join(' ', Enumerable.Repeat("zero", zeros)));
                if (zeros < decimals.Length)
                    words.Append(zeros > 0 ? " " : "").Append(Portuguese(long.Parse(decimals[zeros..], CultureInfo.InvariantCulture)));
            }
            if (m.Groups[3].Success) words.Append(" por cento");
            return words.ToString();
        });

    private static readonly string[] Units =
    [
        "zero", "um", "dois", "três", "quatro", "cinco", "seis", "sete", "oito", "nove", "dez", "onze", "doze", "treze",
        "catorze", "quinze", "dezesseis", "dezessete", "dezoito", "dezenove",
    ];

    private static readonly string[] Tens = ["", "", "vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta", "oitenta", "noventa"];

    private static readonly string[] Hundreds =
        ["", "cento", "duzentos", "trezentos", "quatrocentos", "quinhentos", "seiscentos", "setecentos", "oitocentos", "novecentos"];

    /// <summary>A whole number in Brazilian Portuguese words, up to the billions.</summary>
    public static string Portuguese(long number)
    {
        if (number < 0) return "menos " + Portuguese(-number);
        if (number < 1000) return BelowThousand((int)number);

        var parts = new List<(long Group, string Words)>();
        var billions = number / 1_000_000_000;
        var millions = number / 1_000_000 % 1000;
        var thousands = number / 1000 % 1000;
        var rest = (int)(number % 1000);
        if (billions > 0) parts.Add((billions, billions == 1 ? "um bilhão" : $"{Portuguese(billions)} bilhões"));
        if (millions > 0) parts.Add((millions, millions == 1 ? "um milhão" : $"{BelowThousand((int)millions)} milhões"));
        if (thousands > 0) parts.Add((thousands, thousands == 1 ? "mil" : $"{BelowThousand((int)thousands)} mil"));
        if (rest > 0) parts.Add((rest, BelowThousand(rest)));

        var sb = new StringBuilder(parts[0].Words);
        for (var i = 1; i < parts.Count; i++)
        {
            // "mil e quinhentos", "mil e vinte", "dois milhões e quinhentos mil", but "mil novecentos e quarenta e
            // quatro": the "e" joins only the last group, and only when it is below a hundred or a round hundred.
            var last = i == parts.Count - 1;
            var group = parts[i].Group;
            var joinWithE = last && (group < 100 || group % 100 == 0);
            sb.Append(joinWithE ? " e " : " ").Append(parts[i].Words);
        }
        return sb.ToString();
    }

    private static string BelowThousand(int number)
    {
        if (number < 20) return Units[number];
        if (number == 100) return "cem";
        var hundreds = number / 100;
        var rest = number % 100;
        var words = new List<string>();
        if (hundreds > 0) words.Add(Hundreds[hundreds]);
        if (rest >= 20)
        {
            words.Add(Tens[rest / 10]);
            if (rest % 10 > 0) words.Add(Units[rest % 10]);
        }
        else if (rest > 0)
        {
            words.Add(Units[rest]);
        }
        return string.Join(" e ", words);
    }
}
