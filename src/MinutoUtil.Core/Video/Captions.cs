using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MinutoUtil.Core.Voice;

namespace MinutoUtil.Core.Video;

/// <summary>A few words shown together; the word being spoken is highlighted.</summary>
public sealed record CaptionChunk(IReadOnlyList<SpokenWord> Words, double Start, double End);

/// <summary>
/// Where text may go on a 1080x1920 Short. TikTok and YouTube Shorts cover the bottom ~420 px (caption, music, channel
/// name) and a column of buttons on the right from about y=900 down; the top ~150 px hold the status bar and tabs
/// (centered) and the search or camera buttons (right). Captions sit in the lower third above the bottom overlay and
/// stay narrow enough to clear the button column; the watermark sits top left, below the status bar.
/// </summary>
public static class Layout
{
    public const int Width = 1080;
    public const int Height = 1920;
    public const int CaptionY = 1260;

    /// <summary>Widest a caption line may be: centered, this keeps it left of the right-hand buttons (x > ~960).</summary>
    public const int CaptionMaxWidth = 820;
    public const int StampY = 300;
    public const int YearCardY = 760;
    public const int WatermarkLeft = 54;
    public const int WatermarkTop = 196;

    /// <summary>The emblem watermark: a square this size whose middle lines up with the "Minuto Útil" text.</summary>
    public const int EmblemSize = 96;
    public const int EmblemTop = WatermarkTop + 22 - EmblemSize / 2;
}

/// <summary>
/// Builds the ASS subtitles FFmpeg burns in over the graded picture: word-by-word captions, the opening year card (or
/// the plain date stamp when the card is off) and the watermark.
/// </summary>
public static partial class CaptionBuilder
{
    private const int MaxWordsPerChunk = 4;
    private const int MinWordsPerChunk = 2;
    private const int MaxCharsPerChunk = 18;
    // Roboto Serif is wider than a grotesque: at 96 most chunks need little or no horizontal squeeze.
    private const int CaptionFontSize = 96;
    private const int YearFontSize = 230;

    /// <summary>Black caption outline in pixels: thick enough that red words stay clear of dark footage.</summary>
    private const int CaptionOutline = 8;

    /// <summary>
    /// Advance of each uppercase Roboto Serif Black character as libass draws it, as a share of the font size, without
    /// the style's spacing. Measured with libass (FFmpeg's ass filter): the width of "I" + 20 copies + "I" minus
    /// "I" + 10 copies + "I", over 10. One average does not work for a serif: M (0.744) and W (0.792) are more than
    /// twice as wide as I (0.312), so "MONTGOMERY ROMMEL" overflowed an average that fit "DESEMBARCARAM NAS".
    /// Accented letters take their base letter's width.
    /// </summary>
    private static readonly Dictionary<char, double> CharWidths = new()
    {
        ['A'] = 0.568, ['B'] = 0.576, ['C'] = 0.528, ['D'] = 0.640, ['E'] = 0.504, ['F'] = 0.488, ['G'] = 0.592,
        ['H'] = 0.632, ['I'] = 0.312, ['J'] = 0.488, ['K'] = 0.632, ['L'] = 0.496, ['M'] = 0.744, ['N'] = 0.568,
        ['O'] = 0.632, ['P'] = 0.552, ['Q'] = 0.632, ['R'] = 0.600, ['S'] = 0.480, ['T'] = 0.568, ['U'] = 0.568,
        ['V'] = 0.552, ['W'] = 0.792, ['X'] = 0.560, ['Y'] = 0.540, ['Z'] = 0.496, [' '] = 0.180, ['.'] = 0.240,
        [','] = 0.248, ['!'] = 0.248, ['?'] = 0.456, [':'] = 0.240, [';'] = 0.248, ['-'] = 0.356, ['"'] = 0.344,
        ['('] = 0.280, [')'] = 0.280, ['%'] = 0.584,
    };

    /// <summary>Digits all share one width; anything not measured counts as wide as a W, to stay on the safe side.</summary>
    private const double DigitWidth = 0.432, UnknownWidth = 0.792;

    /// <summary>The caption style's letter spacing, in pixels per character (the Spacing field of the Caption style).</summary>
    private const int CaptionSpacing = 1;

    /// <summary>A chunk stays on screen through a pause shorter than this, so captions do not flicker.</summary>
    private const double MaxHoldGap = 0.6;

    /// <param name="style">The style switches; null turns every layer off (the plain look of version 1).</param>
    /// <param name="emblem">
    /// The emblem picture is overlaid by the renderer, so only its text goes here; false draws the vector hourglass.
    /// </param>
    public static string Build(SpeechTimings timings, double duration, string fontName, string stampFontName,
        string eraStamp, string placeStamp, double stampEnd, StyleOptions? style = null, bool emblem = false)
    {
        style ??= new StyleOptions { YearCard = false, Watermark = false, WordPop = false, KeyWords = false };
        var colors = new Colors(AssColor(style.TextColor), AssColor(style.HighlightColor));
        var (text, highlight) = (colors.Text, colors.Highlight);
        var sb = new StringBuilder()
            .AppendLine("[Script Info]")
            .AppendLine("ScriptType: v4.00+")
            .AppendLine($"PlayResX: {Layout.Width}")
            .AppendLine($"PlayResY: {Layout.Height}")
            .AppendLine("WrapStyle: 2")
            .AppendLine("ScaledBorderAndShadow: yes")
            .AppendLine()
            .AppendLine("[V4+ Styles]")
            .AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding")
            .AppendLine($"Style: Caption,{fontName},{CaptionFontSize},{text},{text},&H00000000,&H90000000,0,0,0,0,100,100,{CaptionSpacing},0,1,{CaptionOutline},4,5,110,110,0,1")
            .AppendLine($"Style: Stamp,{stampFontName},58,{text},{text},&H00000000,&H78000000,0,0,0,0,100,100,4,0,3,18,0,8,80,80,0,1")
            .AppendLine($"Style: Place,{stampFontName},40,{text},{text},&H00000000,&H78000000,0,0,0,0,100,100,3,0,3,14,0,8,80,80,0,1")
            .AppendLine($"Style: Year,{fontName},{YearFontSize},{highlight},{highlight},&H00000000,&H64000000,0,0,0,0,100,100,2,0,1,12,6,5,0,0,0,1")
            .AppendLine($"Style: YearPlace,{stampFontName},66,{text},{text},&H00000000,&H64000000,0,0,0,0,100,100,6,0,1,7,4,8,60,60,0,1")
            .AppendLine($"Style: Mark,{stampFontName},34,{text},{text},&H00000000,&H00000000,0,0,0,0,100,100,1,0,1,2,0,4,0,0,0,1")
            .AppendLine($"Style: Shape,{fontName},20,{text},{text},&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1")
            .AppendLine()
            .AppendLine("[Events]")
            .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        var year = YearOf(eraStamp);
        if (style.YearCard && year is not null)
        {
            AppendYearCard(sb, year.Value, Clean(placeStamp), Math.Min(style.YearCardSeconds, duration));
        }
        else if (Clean(eraStamp).Length > 0 && stampEnd > 0.8)
        {
            var end = Math.Min(stampEnd, duration);
            Event(sb, 1, 0.15, end, "Stamp", $"{{\\an8\\pos({Layout.Width / 2},{Layout.StampY})\\fad(300,300)}}{Clean(eraStamp).ToUpperInvariant()}");
            if (Clean(placeStamp).Length > 0)
                Event(sb, 1, 0.35, end, "Place", $"{{\\an8\\pos({Layout.Width / 2},{Layout.StampY + 108})\\fad(300,300)}}{Clean(placeStamp).ToUpperInvariant()}");
        }
        if (style.Watermark) AppendWatermark(sb, duration, highlight, emblem);

        var keys = style.KeyWords ? KeyWords(timings.Words) : [];
        foreach (var chunk in Chunk(timings.Words))
        {
            if (style.WordPop) AppendRevealChunk(sb, chunk, keys, colors);
            else AppendChunk(sb, chunk, keys, colors);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"(?<!\d)\d{3,4}(?!\d)")]
    private static partial Regex YearNumber();

    /// <summary>The last three- or four-digit number in a date stamp ("6 de junho de 1944" gives 1944), or null.</summary>
    internal static int? YearOf(string? text)
    {
        var matches = YearNumber().Matches(text ?? "");
        return matches.Count == 0 ? null : int.Parse(matches[^1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The opening year card, over the first footage from frame 0: a soft dark band, the year whose last two digits roll
    /// up like a counter (fast at first, then settling, with a small pop on the final value), then the place under it.
    /// Everything fades out at <paramref name="end"/>.
    /// </summary>
    private static void AppendYearCard(StringBuilder sb, int year, string place, double end)
    {
        const double rollEnd = 0.75;
        const int steps = 24;
        var x = Layout.Width / 2 + 4;
        var y = Layout.YearCardY;
        Event(sb, 3, 0, end, "Shape",
            $"{{\\pos(0,{y - 230})\\c&H000000&\\alpha&H70&\\blur18\\fad(0,250)\\p1}}m 0 0 l {Layout.Width} 0 {Layout.Width} 460 0 460{{\\p0}}");

        // The century stays put right of center's left half; the last two digits count up from 24 years earlier.
        Event(sb, 4, 0, end, "Year", $"{{\\an6\\pos({x},{y})\\fad(0,250)}}{year / 100}");
        var times = Enumerable.Range(0, steps + 1).Select(i => rollEnd * (1 - Math.Pow(1 - (double)i / steps, 2.2))).ToList();
        var clip = $"\\clip(0,{y - 150},{Layout.Width},{y + 150})";
        for (var i = 0; i <= steps; i++)
        {
            var value = ((year % 100 - steps + i) % 100 + 100) % 100;
            var start = times[i];
            var stop = i < steps ? times[i + 1] : end;
            if (stop - start < 0.01) continue;
            var motion = i < steps
                ? $"\\move({x},{y + 90},{x},{y},0,{Math.Max(15, (int)((stop - start) * 800))})"
                : $"\\pos({x},{y})\\fscx112\\fscy112\\t(0,140,\\fscx100\\fscy100)\\fad(0,250)";
            Event(sb, 4, start, stop, "Year", $"{{\\an4{motion}{clip}}}{value:00}");
        }
        if (place.Length > 0)
            Event(sb, 4, rollEnd + 0.05, end, "YearPlace", $"{{\\an8\\pos({Layout.Width / 2},{y + 130})\\fad(180,250)}}{place.ToUpperInvariant()}");
    }

    /// <summary>How opaque the watermark is (ASS alpha 0x58, so about 65%); the emblem overlay matches it.</summary>
    public const double WatermarkOpacity = 1 - 0x58 / 255.0;

    /// <summary>
    /// "Minuto Útil" in the top-left corner, semi-transparent, below the status bar and clear of TikTok's and Shorts'
    /// top buttons (top center and top right). The renderer overlays the emblem picture left of it; without the
    /// picture a small hourglass is drawn here as ASS vectors.
    /// </summary>
    private static void AppendWatermark(StringBuilder sb, double duration, string highlight, bool emblem)
    {
        var (left, top) = (Layout.WatermarkLeft, Layout.WatermarkTop);
        if (emblem)
        {
            Event(sb, 5, 0, duration, "Mark", $"{{\\an4\\pos({left + Layout.EmblemSize + 14},{top + 22})\\alpha&H58&}}Minuto Útil");
            return;
        }
        // Cap, upper glass narrowing to the waist, lower glass widening to the base.
        Event(sb, 5, 0, duration, "Shape",
            $"{{\\pos({left},{top})\\c{highlight}\\alpha&H58&\\p1}}m 0 0 l 30 0 30 4 0 4 m 3 6 l 27 6 15 21 m 15 23 l 27 38 3 38 m 0 40 l 30 40 30 44 0 44{{\\p0}}");
        Event(sb, 5, 0, duration, "Mark", $"{{\\an4\\pos({left + 42},{top + 22})\\alpha&H58&}}Minuto Útil");
    }

    private sealed record Colors(string Text, string Highlight);

    [GeneratedRegex("^#?([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})([0-9A-Fa-f]{2})$")]
    private static partial Regex HexColor();

    /// <summary>"#DC010A" as the ASS color "&amp;H000A01DC&amp;" (ASS colors are &amp;HAABBGGRR, alpha 00 is opaque).</summary>
    internal static string AssColor(string hex)
    {
        var match = HexColor().Match(hex?.Trim() ?? "");
        if (!match.Success) throw new ArgumentException($"A style color must be #RRGGBB, not \"{hex}\".", nameof(hex));
        var (r, g, b) = (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
        return $"&H00{b}{g}{r}&".ToUpperInvariant();
    }

    private static readonly HashSet<string> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "dois", "duas", "três", "quatro", "cinco", "seis", "sete", "oito", "nove", "dez", "onze", "doze", "treze", "catorze",
        "quinze", "dezesseis", "dezessete", "dezoito", "dezenove", "vinte", "trinta", "quarenta", "cinquenta", "sessenta",
        "setenta", "oitenta", "noventa", "cem", "cento", "duzentos", "trezentos", "quatrocentos", "quinhentos", "seiscentos",
        "setecentos", "oitocentos", "novecentos", "mil", "milhão", "milhões", "bilhão", "bilhões",
        "janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro",
    };

    private static string Bare(SpokenWord word) => word.Text.Trim('.', ',', '!', '?', ':', ';', '"', '\'');

    private static bool IsNumber(SpokenWord word) => Bare(word).Any(char.IsAsciiDigit) || NumberWords.Contains(Bare(word));

    /// <summary>
    /// Words shown in the highlight color: numbers (in digits or words, with the "e" inside "quarenta e quatro"), months, and
    /// capitalized words that do not start a sentence (names and places).
    /// </summary>
    internal static HashSet<SpokenWord> KeyWords(IReadOnlyList<SpokenWord> words)
    {
        var keys = new HashSet<SpokenWord>();
        for (var i = 0; i < words.Count; i++)
        {
            var bare = Bare(words[i]);
            if (bare.Length == 0) continue;
            var sentenceStart = i == 0 || words[i - 1].Section != words[i].Section || ".!?".Contains(words[i - 1].Text[^1]);
            if (IsNumber(words[i]) || char.IsUpper(bare[0]) && !sentenceStart && bare.Length > 1)
                keys.Add(words[i]);
            else if (bare == "e" && i > 0 && i + 1 < words.Count && IsNumber(words[i - 1]) && IsNumber(words[i + 1])
                     && !",.".Contains(words[i - 1].Text[^1]))
                keys.Add(words[i]);
        }
        return keys;
    }

    /// <summary>
    /// Groups words into chunks of 2 to 4 words and at most <see cref="MaxCharsPerChunk"/> characters, breaking after
    /// punctuation and at section ends. A lone word left over joins its neighbour when it fits.
    /// </summary>
    public static IReadOnlyList<CaptionChunk> Chunk(IReadOnlyList<SpokenWord> words)
    {
        var groups = new List<List<SpokenWord>>();
        List<SpokenWord>? current = null;
        foreach (var word in words)
        {
            var startNew = current is null
                || current.Count >= MaxWordsPerChunk
                || current[^1].Section != word.Section
                || EndsClause(current[^1].Text) && current.Count >= MinWordsPerChunk
                || Length(current) + 1 + word.Text.Length > MaxCharsPerChunk;
            if (startNew)
            {
                current = [];
                groups.Add(current);
            }
            current!.Add(word);
        }

        // Lone words: join the previous chunk (same section, fits), else the next one.
        for (var i = 0; i < groups.Count; i++)
        {
            if (groups[i].Count != 1) continue;
            var word = groups[i][0];
            if (i > 0 && groups[i - 1][^1].Section == word.Section && groups[i - 1].Count < MaxWordsPerChunk
                && Length(groups[i - 1]) + 1 + word.Text.Length <= MaxCharsPerChunk + 2)
            {
                groups[i - 1].Add(word);
                groups.RemoveAt(i--);
            }
            else if (i + 1 < groups.Count && groups[i + 1][0].Section == word.Section && groups[i + 1].Count < MaxWordsPerChunk
                     && Length(groups[i + 1]) + 1 + word.Text.Length <= MaxCharsPerChunk + 2)
            {
                groups[i + 1].Insert(0, word);
                groups.RemoveAt(i--);
            }
        }

        var chunks = new List<CaptionChunk>(groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var end = group[^1].End + 0.2;
            if (i + 1 < groups.Count)
            {
                var next = groups[i + 1][0];
                if (next.Section == group[^1].Section && next.Start - group[^1].End < MaxHoldGap) end = next.Start;
                end = Math.Min(end, next.Start);
            }
            chunks.Add(new CaptionChunk(group, group[0].Start, Math.Max(end, group[^1].End)));
        }
        return chunks;
    }

    private static int Length(List<SpokenWord> words) => words.Sum(w => w.Text.Length) + words.Count - 1;

    /// <summary>Long chunks are squeezed horizontally to stay clear of the right-hand buttons.</summary>
    internal static int Squeeze(CaptionChunk chunk)
    {
        var estimated = CaptionWidth(string.Join(' ', chunk.Words.Select(w => Clean(w.Text).ToUpperInvariant())));
        return estimated > Layout.CaptionMaxWidth ? (int)Math.Floor(100 * Layout.CaptionMaxWidth / estimated) : 100;
    }

    /// <summary>How wide libass draws an uppercase caption line at full width, in pixels, without the outline.</summary>
    internal static double CaptionWidth(string text) =>
        text.Sum(c => CharWidth(c) * CaptionFontSize + CaptionSpacing);

    private static double CharWidth(char c)
    {
        if (char.IsAsciiDigit(c)) return DigitWidth;
        if (CharWidths.TryGetValue(c, out var width)) return width;
        // "Ã" is "A" plus a combining tilde.
        var bare = c.ToString().Normalize(NormalizationForm.FormD)[0];
        return CharWidths.TryGetValue(bare, out width) ? width : UnknownWidth;
    }

    /// <summary>Version 1's captions: the whole chunk shown, the spoken word highlighted (key words too).</summary>
    private static void AppendChunk(StringBuilder sb, CaptionChunk chunk, HashSet<SpokenWord> keys, Colors colors)
    {
        var squeeze = Squeeze(chunk);
        for (var i = 0; i < chunk.Words.Count; i++)
        {
            var start = i == 0 ? chunk.Start : chunk.Words[i].Start;
            var end = i + 1 < chunk.Words.Count ? chunk.Words[i + 1].Start : chunk.End;
            if (end <= start) continue;

            var line = new StringBuilder($"{{\\an5\\pos({Layout.Width / 2},{Layout.CaptionY})\\q2\\fscx{squeeze}");
            // The chunk pops in slightly when it first appears.
            line.Append(i == 0 ? "\\fscy88\\t(0,90,\\fscy100)}" : "}");
            for (var w = 0; w < chunk.Words.Count; w++)
            {
                if (w > 0) line.Append(' ');
                var word = Clean(chunk.Words[w].Text).ToUpperInvariant();
                var color = w == i || keys.Contains(chunk.Words[w]) ? colors.Highlight : colors.Text;
                line.Append($"{{\\c{color}}}{word}");
            }
            Event(sb, 2, start, end, "Caption", line.ToString());
        }
    }

    /// <summary>
    /// The chunk builds up word by word as it is spoken and the newest word pops in (125% to 100% in 110 ms). Words not
    /// yet spoken are already in the line but invisible, so the line does not jump sideways as it fills. Key words
    /// are in the highlight color, the rest in the text color.
    /// </summary>
    private static void AppendRevealChunk(StringBuilder sb, CaptionChunk chunk, HashSet<SpokenWord> keys, Colors colors)
    {
        var squeeze = Squeeze(chunk);
        var pop = squeeze * 125 / 100;
        for (var i = 0; i < chunk.Words.Count; i++)
        {
            var start = i == 0 ? chunk.Start : chunk.Words[i].Start;
            var end = i + 1 < chunk.Words.Count ? chunk.Words[i + 1].Start : chunk.End;
            if (end <= start) continue;
            var line = new StringBuilder($"{{\\an5\\pos({Layout.Width / 2},{Layout.CaptionY})\\q2\\fscx{squeeze}}}");
            for (var w = 0; w < chunk.Words.Count; w++)
            {
                if (w > 0) line.Append(' ');
                var word = Clean(chunk.Words[w].Text).ToUpperInvariant();
                var color = keys.Contains(chunk.Words[w]) ? colors.Highlight : colors.Text;
                if (w < i) line.Append($"{{\\alpha&H00&\\c{color}}}{word}");
                else if (w == i) line.Append($"{{\\alpha&H00&\\c{color}\\fscx{pop}\\fscy125\\t(0,110,\\fscx{squeeze}\\fscy100)}}{word}{{\\fscx{squeeze}\\fscy100}}");
                else line.Append($"{{\\alpha&HFF&}}{word}");
            }
            Event(sb, 2, start, end, "Caption", line.ToString());
        }
    }

    private static bool EndsClause(string word) => word.Length > 0 && ".,!?:;".Contains(word[^1]);

    /// <summary>Text safe for ASS: braces and backslashes would start override tags; emoji are not in the font.</summary>
    internal static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '{' or '}' or '\\') continue;
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.IsBmp && category is not (UnicodeCategory.OtherSymbol or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                    or UnicodeCategory.Control or UnicodeCategory.Format))
                sb.Append(char.ConvertFromUtf32(rune.Value));
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void Event(StringBuilder sb, int layer, double start, double end, string style, string text) =>
        sb.AppendLine($"Dialogue: {layer},{Time(start)},{Time(end)},{style},,0,0,0,,{text}");

    /// <summary>ASS time format H:MM:SS.cc (centiseconds).</summary>
    internal static string Time(double seconds)
    {
        var cs = (long)Math.Round(Math.Max(0, seconds) * 100);
        return $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}";
    }
}
