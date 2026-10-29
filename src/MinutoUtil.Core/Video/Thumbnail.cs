using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MinutoUtil.Core.Video;

/// <summary>A word on the thumbnail and whether it is in the highlight color.</summary>
public sealed record ThumbnailWord(string Text, bool Highlight);

/// <summary>The thumbnail's text laid out: the year line (or null), the lines of words and their font size.</summary>
public sealed record ThumbnailLayout(string? Year, IReadOnlyList<IReadOnlyList<ThumbnailWord>> Lines, int FontSize);

/// <summary>
/// The cover image for YouTube (custom thumbnail) and TikTok (the owner picks it in the app): a picture from the video
/// with the same grade, the year in red like the year card, two to five words in the caption font with one word in
/// red, and the emblem watermark. 1080x1920 JPEG under YouTube's 2 MB limit.
/// </summary>
public static partial class Thumbnail
{
    public const string FileName = "thumbnail.jpg";

    /// <summary>YouTube's limit for a custom thumbnail is 2 MB.</summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The text block's middle. TikTok's profile grid shows covers cropped to 3:4 (y 240 to 1680), and the Shorts shelf
    /// puts the title over the bottom, so the block sits a little above the middle of the frame.
    /// </summary>
    internal const int CenterY = 900;

    /// <summary>Widest a line may be, so the outline stays inside the frame with a margin.</summary>
    internal const int MaxLineWidth = 920;

    internal const int MaxLines = 3;
    internal const int MaxFontSize = 150;
    internal const int MinFontSize = 84;
    private const int YearFontSize = 210;
    private const int Spacing = 1;
    private const int Outline = 10;

    /// <summary>
    /// The words of <paramref name="text"/>, uppercase as the captions are; words between asterisks are highlighted
    /// ("O bilhete que *ninguém* leu"). An asterisk left open highlights to the end.
    /// </summary>
    public static IReadOnlyList<ThumbnailWord> Words(string text)
    {
        var words = new List<ThumbnailWord>();
        var highlight = false;
        foreach (var raw in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith('*')) highlight = true;
            var word = CaptionBuilder.Clean(raw.Replace("*", "")).ToUpperInvariant();
            if (word.Length > 0) words.Add(new ThumbnailWord(word, highlight));
            if (raw.TrimEnd('.', ',', '!', '?', ':', ';').EndsWith('*')) highlight = false;
        }
        return words;
    }

    [GeneratedRegex(@"(?<!\d)(\d{1,4})\s*(a\.\s*C\.?|antes de Cristo)", RegexOptions.IgnoreCase)]
    private static partial Regex YearBeforeChrist();

    /// <summary>The year shown above the words: "1944" from "6 de junho de 1944", "44 A.C." from "15 de março de 44 a.C.", or null.</summary>
    internal static string? YearLabel(string? eraStamp)
    {
        if (YearBeforeChrist().Match(eraStamp ?? "") is { Success: true } bc) return $"{bc.Groups[1].Value} A.C.";
        return CaptionBuilder.YearOf(eraStamp)?.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The largest font size whose lines fit: for each size from the largest down, the fewest lines (at most
    /// <see cref="MaxLines"/>) with the words split so the longest line is as short as possible, which avoids a lone
    /// word on the last line. When nothing fits, the smallest size with as many lines as it takes.
    /// </summary>
    public static ThumbnailLayout Arrange(IReadOnlyList<ThumbnailWord> words, string? year)
    {
        if (words.Count == 0) return new ThumbnailLayout(year, [], MaxFontSize);
        for (var size = MaxFontSize; size >= MinFontSize; size -= 6)
            for (var lines = 1; lines <= Math.Min(MaxLines, words.Count); lines++)
                if (Balanced(words, lines, size) is { } split && split.Max(l => LineWidth(l, size)) <= MaxLineWidth)
                    return new ThumbnailLayout(year, split, size);
        // Too long for three lines even small (a long title as the fallback): one more line at a time, and a word
        // that is too wide alone gets squeezed (see Ass).
        for (var lines = MaxLines + 1; lines < words.Count; lines++)
            if (Balanced(words, lines, MinFontSize) is { } split && split.Max(l => LineWidth(l, MinFontSize)) <= MaxLineWidth)
                return new ThumbnailLayout(year, split, MinFontSize);
        return new ThumbnailLayout(year, words.Select(w => (IReadOnlyList<ThumbnailWord>)[w]).ToList(), MinFontSize);
    }

    internal static double LineWidth(IReadOnlyList<ThumbnailWord> line, int fontSize) =>
        CaptionBuilder.TextWidth(string.Join(' ', line.Select(w => w.Text)), fontSize, Spacing);

    /// <summary>The words cut into exactly <paramref name="lines"/> lines in order, the longest line as short as possible.</summary>
    private static IReadOnlyList<IReadOnlyList<ThumbnailWord>>? Balanced(IReadOnlyList<ThumbnailWord> words, int lines, int size)
    {
        if (lines > words.Count) return null;
        IReadOnlyList<IReadOnlyList<ThumbnailWord>>? best = null;
        var bestWidth = double.MaxValue;
        void Search(int start, List<IReadOnlyList<ThumbnailWord>> taken)
        {
            if (taken.Count == lines - 1)
            {
                var last = words.Skip(start).ToList();
                var widest = Math.Max(taken.Count == 0 ? 0 : taken.Max(l => LineWidth(l, size)), LineWidth(last, size));
                if (widest < bestWidth) (best, bestWidth) = ([.. taken, last], widest);
                return;
            }
            // Leave at least one word for each line still to come.
            for (var end = start + 1; end <= words.Count - (lines - 1 - taken.Count); end++)
            {
                taken.Add(words.Skip(start).Take(end - start).ToList());
                Search(end, taken);
                taken.RemoveAt(taken.Count - 1);
            }
        }
        Search(0, []);
        return best;
    }

    /// <summary>
    /// The ASS file burned over the graded picture: a soft dark band behind the block (like the year card's), the year
    /// in the highlight color, the words in the text color with the highlighted ones in red, and the watermark text.
    /// </summary>
    public static string Ass(ThumbnailLayout layout, string fontName, string stampFontName, StyleOptions style, bool emblem)
    {
        var text = CaptionBuilder.AssColor(style.TextColor);
        var highlight = CaptionBuilder.AssColor(style.HighlightColor);
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
            .AppendLine($"Style: Words,{fontName},{layout.FontSize},{text},{text},&H00000000,&H90000000,0,0,0,0,100,100,{Spacing},0,1,{Outline},6,5,0,0,0,1")
            .AppendLine($"Style: Year,{fontName},{YearFontSize},{highlight},{highlight},&H00000000,&H64000000,0,0,0,0,100,100,2,0,1,12,6,5,0,0,0,1")
            .AppendLine($"Style: Mark,{stampFontName},34,{text},{text},&H00000000,&H00000000,0,0,0,0,100,100,1,0,1,2,0,4,0,0,0,1")
            .AppendLine($"Style: Shape,{fontName},20,{text},{text},&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1")
            .AppendLine()
            .AppendLine("[Events]")
            .AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        var lineHeight = (int)Math.Round(layout.FontSize * 1.1);
        var yearHeight = layout.Year is null ? 0 : (int)Math.Round(YearFontSize * 0.95);
        var gap = layout.Year is null || layout.Lines.Count == 0 ? 0 : 24;
        var height = yearHeight + gap + layout.Lines.Count * lineHeight;
        var top = CenterY - height / 2;
        var x = Layout.Width / 2;

        Event(sb, 1, "Shape", $"{{\\pos(0,{top - 110})\\c&H000000&\\alpha&H70&\\blur24\\p1}}m 0 0 l {Layout.Width} 0 {Layout.Width} {height + 220} 0 {height + 220}{{\\p0}}");
        if (layout.Year is not null) Event(sb, 2, "Year", $"{{\\an5\\pos({x},{top + yearHeight / 2})}}{layout.Year}");
        for (var i = 0; i < layout.Lines.Count; i++)
        {
            var line = layout.Lines[i];
            // Squeezed only when one word alone is wider than the frame allows.
            var squeeze = Math.Min(100, (int)Math.Floor(100 * MaxLineWidth / LineWidth(line, layout.FontSize)));
            var y = top + yearHeight + gap + i * lineHeight + lineHeight / 2;
            var words = string.Join(' ', line.Select(w => $"{{\\c{(w.Highlight ? highlight : text)}}}{w.Text}"));
            Event(sb, 2, "Words", $"{{\\an5\\pos({x},{y})\\q2\\fscx{squeeze}}}{words}");
        }
        if (style.Watermark && emblem)
            Event(sb, 3, "Mark", $"{{\\an4\\pos({Layout.WatermarkLeft + Layout.EmblemSize + 14},{Layout.WatermarkTop + 22})}}{CaptionBuilder.Clean(style.WatermarkText)}");
        return sb.ToString();
    }

    private static void Event(StringBuilder sb, int layer, string style, string text) =>
        sb.AppendLine($"Dialogue: {layer},0:00:00.00,0:00:10.00,{style},,0,0,0,,{text}");

    /// <summary>
    /// Renders the thumbnail into <paramref name="outputPath"/>. FFmpeg runs in <paramref name="workFolder"/>, which
    /// holds fonts/ (as for the video), so the filter graph needs no Windows paths. The picture fills the frame (cropped
    /// to 9:16 around its middle) and gets the video's grade; the JPEG quality drops until the file is under 2 MB.
    /// </summary>
    public static async Task RenderAsync(VideoOptions options, string imagePath, string text, string? eraStamp, string workFolder,
        string? emblemPath, string outputPath, CancellationToken ct)
    {
        var layout = Arrange(Words(text), YearLabel(eraStamp));
        var assName = "thumbnail.ass";
        await File.WriteAllTextAsync(Path.Combine(workFolder, assName),
            Ass(layout, options.FontName, options.StampFontName, options.Style, emblemPath is not null), ct);

        var (w, h) = (options.Width, options.Height);
        var picture = new FinalRenderer(options, new Ffmpeg(options)).PictureChain(0);
        var graph = new StringBuilder($"[0:v]scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h},setsar=1,format=yuv420p,{picture}");
        if (emblemPath is not null)
            graph.Append("[pic];[1:v]format=rgba[emblem];")
                .Append($"[pic][emblem]overlay=x={Layout.WatermarkLeft}:y={Layout.EmblemTop}:format=auto");
        graph.Append($",ass={assName}:fontsdir=fonts,format=yuvj420p[v]");

        var temp = outputPath + ".partial.jpg";
        foreach (var quality in new[] { 2, 4, 6, 9, 13 })
        {
            List<string> args = ["-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-i", Path.GetFullPath(imagePath)];
            if (emblemPath is not null) args.AddRange(["-i", Path.GetFullPath(emblemPath)]);
            args.AddRange(["-filter_complex", graph.ToString(), "-map", "[v]", "-frames:v", "1", "-q:v", quality.ToString(CultureInfo.InvariantCulture),
                Path.GetFullPath(temp)]);
            await ProcessRunner.RunCheckedAsync(options.FfmpegPath, args, workFolder, TimeSpan.FromMinutes(2), ct);
            if (new FileInfo(temp).Length <= MaxBytes) break;
        }
        var bytes = new FileInfo(temp).Length;
        if (bytes > MaxBytes) throw new ToolException($"The thumbnail is {bytes} bytes even at low quality; YouTube takes at most {MaxBytes}.");
        File.Move(temp, outputPath, overwrite: true);
        Log.Info($"thumbnail: {Path.GetFileName(outputPath)}, {bytes / 1024} KB, {layout.Lines.Count} line(s) at {layout.FontSize} px" +
                 (layout.Year is null ? "" : $", year {layout.Year}"));
    }
}
