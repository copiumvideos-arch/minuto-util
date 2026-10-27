using System.Globalization;
using System.Text;
using MinutoUtil.Core.Voice;

namespace MinutoUtil.Core.Video;

/// <summary>A few words shown together; the word being spoken is highlighted.</summary>
public sealed record CaptionChunk(IReadOnlyList<SpokenWord> Words, double Start, double End);

/// <summary>
/// Where text may go on a 1080x1920 Short. TikTok and YouTube Shorts cover the bottom ~420 px (caption, music, channel
/// name) and a column of buttons on the right from about y=900 down; the top ~200 px hold the search bar and tabs.
/// Captions sit in the lower third above the bottom overlay and stay narrow enough to clear the button column.
/// </summary>
public static class Layout
{
    public const int Width = 1080;
    public const int Height = 1920;
    public const int CaptionY = 1260;

    /// <summary>Widest a caption line may be: centered, this keeps it left of the right-hand buttons (x > ~960).</summary>
    public const int CaptionMaxWidth = 820;
    public const int StampY = 300;
}

/// <summary>Builds the ASS subtitles FFmpeg burns in: word-by-word captions and the date stamp on the first shot.</summary>
public static class CaptionBuilder
{
    private const int MaxWordsPerChunk = 4;
    private const int MinWordsPerChunk = 2;
    private const int MaxCharsPerChunk = 18;
    private const int CaptionFontSize = 104;

    /// <summary>
    /// Average advance of an uppercase Montserrat Black letter or space as libass draws it, as a share of the font size
    /// (measured: "QUARENTA E QUATRO." at size 92 is about 750 px wide), rounded up to stay on the safe side.
    /// </summary>
    private const double CharWidth = 0.48;

    /// <summary>A chunk stays on screen through a pause shorter than this, so captions do not flicker.</summary>
    private const double MaxHoldGap = 0.6;

    // ASS colors are &HAABBGGRR.
    private const string Highlight = "&H0000D7FF&";
    private const string White = "&H00FFFFFF&";

    public static string Build(SpeechTimings timings, double duration, string fontName, string stampFontName,
        string eraStamp, string placeStamp, double stampEnd)
    {
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
            .AppendLine($"Style: Caption,{fontName},{CaptionFontSize},{White},{White},&H00000000,&H90000000,0,0,0,0,100,100,1,0,1,8,4,5,110,110,0,1")
            .AppendLine($"Style: Stamp,{stampFontName},58,{White},{White},&H00000000,&H78000000,0,0,0,0,100,100,4,0,3,18,0,8,80,80,0,1")
            .AppendLine($"Style: Place,{stampFontName},40,&H00E8E8E8&,{White},&H00000000,&H78000000,0,0,0,0,100,100,3,0,3,14,0,8,80,80,0,1")
            .AppendLine()
            .AppendLine("[Events]")
            .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        if (Clean(eraStamp).Length > 0 && stampEnd > 0.8)
        {
            var end = Math.Min(stampEnd, duration);
            Event(sb, 1, 0.15, end, "Stamp", $"{{\\an8\\pos({Layout.Width / 2},{Layout.StampY})\\fad(300,300)}}{Clean(eraStamp).ToUpperInvariant()}");
            if (Clean(placeStamp).Length > 0)
                Event(sb, 1, 0.35, end, "Place", $"{{\\an8\\pos({Layout.Width / 2},{Layout.StampY + 108})\\fad(300,300)}}{Clean(placeStamp).ToUpperInvariant()}");
        }

        foreach (var chunk in Chunk(timings.Words))
            AppendChunk(sb, chunk);
        return sb.ToString();
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

    private static void AppendChunk(StringBuilder sb, CaptionChunk chunk)
    {
        var text = string.Join(' ', chunk.Words.Select(w => Clean(w.Text).ToUpperInvariant()));
        // Long chunks are squeezed horizontally to stay clear of the right-hand buttons.
        var estimated = text.Length * CaptionFontSize * CharWidth;
        var squeeze = estimated > Layout.CaptionMaxWidth ? (int)Math.Floor(100 * Layout.CaptionMaxWidth / estimated) : 100;
        for (var i = 0; i < chunk.Words.Count; i++)
        {
            var start = i == 0 ? chunk.Start : chunk.Words[i].Start;
            var end = i + 1 < chunk.Words.Count ? chunk.Words[i + 1].Start : chunk.End;
            if (end <= start) continue;

            var line = new StringBuilder($"{{\\an5\\pos({Layout.Width / 2},{Layout.CaptionY})\\q2\\fscx{squeeze}");
            // The chunk pops in slightly when it first appears.
            line.Append(i == 0 ? $"\\fscy88\\t(0,90,\\fscy100)}}" : "}");
            for (var w = 0; w < chunk.Words.Count; w++)
            {
                if (w > 0) line.Append(' ');
                var word = Clean(chunk.Words[w].Text).ToUpperInvariant();
                line.Append(w == i ? $"{{\\c{Highlight}}}{word}{{\\c{White}}}" : word);
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
