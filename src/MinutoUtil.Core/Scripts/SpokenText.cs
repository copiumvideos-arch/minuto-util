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

    /// <summary>A free game said like a price: "por gratuito", "por grátis", "por zero reais", "por R$ 0".</summary>
    [GeneratedRegex(@"(?<!\w)por\s+(?:gratuito|gratuita|grátis|gratis|zero\s+reais|R\$\s*0(?:,00)?)(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FreeAsPrice();

    /// <summary>
    /// "ROB RUN chegou ao PC por gratuito" becomes "ROB RUN chegou ao PC de graça": nobody says a free game costs
    /// "gratuito" (the ROB RUN video of 8/11/2026 said it in the title and the narration).
    /// </summary>
    public static string WithFreeSaidPlainly(string text) => FreeAsPrice().Replace(text, "de graça");

    // Number words that make a short phrase a fact ("até quatro pessoas", "noventa e nove reais"); "um" and "uma" are
    // left out, since they are mostly articles.
    private static readonly HashSet<string> NumberWords =
    [
        "dois", "duas", "tres", "quatro", "cinco", "seis", "sete", "oito", "nove", "dez", "onze", "doze", "treze", "catorze",
        "quatorze", "quinze", "dezesseis", "dezessete", "dezoito", "dezenove", "vinte", "trinta", "quarenta", "cinquenta",
        "sessenta", "setenta", "oitenta", "noventa", "cem", "cento", "duzentos", "trezentos", "quatrocentos", "quinhentos",
        "seiscentos", "setecentos", "oitocentos", "novecentos", "mil", "milhao", "milhoes", "bilhao", "bilhoes", "reais",
        "centavos", "dolares", "gratuito", "gratis", "graca",
    ];

    /// <summary>
    /// The first phrase said twice in a narration, or null: any four words in a row, or three in a row with a number or
    /// a price in them ("até quatro pessoas"). A short news video that says a fact twice wastes the viewer's time (the
    /// Shrek 2 video said "até quatro pessoas" twice, ROB RUN said "o jogo tem um sistema de" twice). Words that are
    /// all part of one figure ("noventa e nove" in "noventa e nove reais e noventa e nove centavos") are not a phrase.
    /// </summary>
    public static string? RepeatedPhrase(IEnumerable<string> spoken)
    {
        var words = string.Join(" ", spoken.Select(TextUtil.Fold)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var size in new[] { 4, 3 })
        {
            var first = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i + size <= words.Length; i++)
            {
                var gram = words[i..(i + size)];
                if (gram.All(w => w == "e" || NumberWords.Contains(w))) continue;
                if (size == 3 && !gram.Any(NumberWords.Contains)) continue;
                var phrase = string.Join(' ', gram);
                if (!first.TryAdd(phrase, i) && i - first[phrase] >= size) return phrase;
            }
        }
        return null;
    }

    // A year split in two the English way ("fifteen ninety"): a number from eleven to nineteen followed straight by a
    // tens word ("quinze noventa", "dezenove quarenta e quatro"), another teen ("dezenove dezoito") or "zero" and a
    // unit ("dezenove zero cinco"). Portuguese never puts two such numbers side by side.
    [GeneratedRegex(@"(?<!\w)(onze|doze|treze|catorze|quatorze|quinze|dezesseis|dezessete|dezoito|dezenove)\s+(?:(vinte|trinta|quarenta|cinquenta|sessenta|setenta|oitenta|noventa)(?:\s+e\s+(um|dois|três|quatro|cinco|seis|sete|oito|nove))?|(dez|onze|doze|treze|catorze|quatorze|quinze|dezesseis|dezessete|dezoito|dezenove)|zero\s+(um|dois|três|quatro|cinco|seis|sete|oito|nove))(?!\w)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitYear();

    /// <summary>
    /// Years a writer split the English way said in full, as Brazilians say them: "na década de quinze noventa" becomes
    /// "na década de mil quinhentos e noventa" (the Palmares video read the split form aloud).
    /// </summary>
    public static string WithFullYears(string text) =>
        SplitYear().Replace(text, m =>
        {
            static int Value(Group g) => g.Value.ToLowerInvariant() switch
            {
                "quatorze" => 14,
                var w when Array.IndexOf(Tens, w) is > 1 and var t => t * 10,
                var w => Array.IndexOf(Units, w),
            };
            var rest = m.Groups[2].Success ? Value(m.Groups[2]) + (m.Groups[3].Success ? Value(m.Groups[3]) : 0)
                : m.Groups[4].Success ? Value(m.Groups[4]) : Value(m.Groups[5]);
            var words = Portuguese(Value(m.Groups[1]) * 100 + rest);
            return char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(words[0]) + words[1..] : words;
        });

    /// <summary>Each whole word in <paramref name="respellings"/> written the way the voice must say it: "GTA seis" becomes "gê tê á seis".</summary>
    public static string Pronounce(string text, IReadOnlyDictionary<string, string> respellings) =>
        respellings.Aggregate(text, (current, r) =>
            Regex.Replace(current, $@"(?<!\w){Regex.Escape(r.Key)}(?!\w)", r.Value.Replace("$", "$$"), RegexOptions.CultureInvariant));

    /// <summary>"AU$ 57 mil", "US$1,5 milhão", "AUD 35.000", "R$ 20": a sum with its currency before or after it.</summary>
    [GeneratedRegex(@"(?<![\w$])(?:(R|US|AU|A|CA|C|NZ)\$|(AUD|USD|BRL|CAD|NZD|EUR|GBP|JPY)\b|(€|£|¥)|\$)\s?((?:\d{1,3}(?:\.\d{3})+|\d+)(?:,\d+)?)(\s(?:mil|milhões|milhão|bilhões|bilhão)\b)?|(?<!\w)((?:\d{1,3}(?:\.\d{3})+|\d+)(?:,\d+)?)(\s(?:mil|milhões|milhão|bilhões|bilhão)\b)?\s(AUD|USD|BRL|CAD|NZD|EUR|GBP|JPY)\b")]
    private static partial Regex Money();

    /// <summary>How a narrator names each currency after the amount; a code or symbol is never read aloud ("AU dólares").</summary>
    private static readonly Dictionary<string, (string One, string Many)> Currencies = new()
    {
        ["R"] = ("real", "reais"), ["BRL"] = ("real", "reais"),
        ["US"] = ("dólar", "dólares"), ["USD"] = ("dólar", "dólares"), [""] = ("dólar", "dólares"),
        ["AU"] = ("dólar australiano", "dólares australianos"), ["A"] = ("dólar australiano", "dólares australianos"),
        ["AUD"] = ("dólar australiano", "dólares australianos"),
        ["CA"] = ("dólar canadense", "dólares canadenses"), ["C"] = ("dólar canadense", "dólares canadenses"),
        ["CAD"] = ("dólar canadense", "dólares canadenses"),
        ["NZ"] = ("dólar neozelandês", "dólares neozelandeses"), ["NZD"] = ("dólar neozelandês", "dólares neozelandeses"),
        ["€"] = ("euro", "euros"), ["EUR"] = ("euro", "euros"),
        ["£"] = ("libra", "libras"), ["GBP"] = ("libra", "libras"),
        ["¥"] = ("iene", "ienes"), ["JPY"] = ("iene", "ienes"),
    };

    /// <summary>
    /// Sums of money in words with the currency after them: "AU$ 57 mil" becomes "cinquenta e sete mil dólares
    /// australianos", "US$ 1,5 milhão" "um vírgula cinco milhão de dólares". Then the remaining numbers are spelled out.
    /// </summary>
    public static string SpellNumbers(string text) =>
        SpellPlainNumbers(Money().Replace(text, m =>
        {
            var before = m.Groups[4].Success;
            var currency = before ? m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value
                : m.Groups[8].Value;
            var amount = before ? m.Groups[4].Value : m.Groups[6].Value;
            var scale = (before ? m.Groups[5].Value : m.Groups[7].Value).Trim();
            var (one, many) = Currencies[currency];
            var single = scale.Length == 0 && amount == "1";
            // "um milhão de dólares", "dois bilhões de reais", but "cinquenta mil dólares".
            var of = scale is "milhão" or "milhões" or "bilhão" or "bilhões" ? " de" : "";
            return $"{amount}{(scale.Length > 0 ? " " + scale : "")}{of} {(single ? one : many)}";
        }));

    /// <summary>The text with each number written out: "Em 1944, 24.000 soldados" becomes "Em mil novecentos e quarenta e quatro, vinte e quatro mil soldados".</summary>
    private static string SpellPlainNumbers(string text) =>
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
