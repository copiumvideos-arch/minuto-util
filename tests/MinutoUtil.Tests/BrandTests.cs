using System.Globalization;
using MinutoUtil.Core;
using MinutoUtil.Core.Video;
using MinutoUtil.Core.Voice;

namespace MinutoUtil.Tests;

/// <summary>
/// The channel brand: cream and red captions in Roboto Serif, the emblem watermark, and long captions that stay inside
/// the safe area as libass really draws them (real fonts, real FFmpeg).
/// </summary>
public sealed class BrandTests
{
    private const string Black = "Roboto Serif Black";
    private const string ExtraBold = "Roboto Serif ExtraBold";

    private static SpeechTimings Words(params string[] texts)
    {
        var words = texts.Select((text, i) => new SpokenWord(text, i * 0.5, i * 0.5 + 0.45, 0)).ToList();
        return new SpeechTimings(texts.Length * 0.5, [new SectionTime(0, texts.Length * 0.5)], words, []);
    }

    [Theory]
    [InlineData("#F7E2C7", "&H00C7E2F7&")]
    [InlineData("#DC010A", "&H000A01DC&")]
    [InlineData("ffd700", "&H0000D7FF&")]
    public void HexColorsBecomeAssColors(string hex, string ass) => Assert.Equal(ass, CaptionBuilder.AssColor(hex));

    [Theory]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#GG0000")]
    [InlineData("&H00FFFFFF&")]
    public void BadColorsAreRefused(string hex) => Assert.Throws<ArgumentException>(() => CaptionBuilder.AssColor(hex));

    [Fact]
    public void DefaultsAreCreamTextAndRedHighlightsInRobotoSerif()
    {
        var video = new VideoOptions();
        Assert.Equal(Black, video.FontName);
        Assert.Equal(ExtraBold, video.StampFontName);
        Assert.True(File.Exists(Path.Combine(TestPaths.RepositoryRoot, video.FontFile)));
        Assert.True(File.Exists(Path.Combine(TestPaths.RepositoryRoot, video.StampFontFile)));
        Assert.True(File.Exists(Path.Combine(TestPaths.RepositoryRoot, video.Style.WatermarkImage)));

        var ass = CaptionBuilder.Build(Words("Em", "seis", "de", "junho,", "Eisenhower", "decidiu."), 4, video.FontName, video.StampFontName,
            "6 de junho de 1944", "Normandia, França", 3.5, new StyleOptions());
        Assert.Contains($"Style: Caption,{Black},96,&H00C7E2F7&,", ass);
        Assert.Contains($"Style: Year,{Black},230,&H000A01DC&,", ass);
        Assert.Contains($"Style: YearPlace,{ExtraBold},66,&H00C7E2F7&,", ass);
        Assert.Contains($"Style: Mark,{ExtraBold},", ass);
        // Words already spoken in the chunk: the number in red, the rest cream.
        Assert.Contains(@"\c&H000A01DC&}SEIS", ass);
        Assert.Contains(@"\c&H00C7E2F7&}DE", ass);
        Assert.DoesNotContain("D7FF", ass); // no yellow left
    }

    [Fact]
    public void A_year_before_Christ_gets_the_plain_stamp_and_a_year_after_Christ_the_rolling_card()
    {
        var timings = Words("Em", "Roma");
        var before = CaptionBuilder.Build(timings, 4, Black, ExtraBold, "por volta de 1274 a.C.", "Cades, Síria", 3.5, new StyleOptions());
        var after = CaptionBuilder.Build(timings, 4, Black, ExtraBold, "4 de setembro de 476", "Ravena, Itália", 3.5, new StyleOptions());

        // The rolling card has no room for "a.C.": a year before Christ would read as one after it.
        Assert.DoesNotContain(",Year,", before);
        Assert.Contains("}POR VOLTA DE 1274 A.C.", before);
        Assert.Contains(",Year,", after);
        Assert.Contains("}76", after);
        Assert.Null(CaptionBuilder.YearOf("15 de março de 44 a.C."));
        Assert.Equal(1274, CaptionBuilder.YearOf("1274 d.C."));
    }

    [Fact]
    public void ColorsComeFromTheStyle()
    {
        var style = new StyleOptions { TextColor = "#FFFFFF", HighlightColor = "#00FF00" };
        var ass = CaptionBuilder.Build(Words("O", "general", "Eisenhower", "chegou"), 2, Black, ExtraBold, "", "", 0, style);
        Assert.Contains(@"\c&H0000FF00&}EISENHOWER", ass);
        Assert.Contains(@"\c&H00FFFFFF&}O ", ass);
    }

    [Fact]
    public void WithTheEmblemOnlyTheTextIsDrawnElseTheHourglass()
    {
        var timings = Words("O", "general", "Eisenhower");
        // The live channel's style: its watermark text is the channel name from channels/minuto-util/config.json.
        var minuto = PublishData.Minuto.Config.Video.Style;
        var withEmblem = CaptionBuilder.Build(timings, 2, Black, ExtraBold, "", "", 0, minuto, emblem: true);
        Assert.Contains($@"\pos({Layout.WatermarkLeft + Layout.EmblemSize + 14},{Layout.WatermarkTop + 22})\alpha&H58&}}Minuto Útil", withEmblem);
        Assert.DoesNotContain("m 0 0 l 30 0", withEmblem);

        var hourglass = CaptionBuilder.Build(timings, 2, Black, ExtraBold, "", "", 0, minuto, emblem: false);
        Assert.Contains(@"\c&H000A01DC&\alpha&H58&\p1}m 0 0 l 30 0", hourglass);
        Assert.Contains($@"\pos({Layout.WatermarkLeft + 42},{Layout.WatermarkTop + 22})", hourglass);
    }

    [Fact]
    public void TheEmblemIsOverlaidOnThePictureUnderTheCaptions()
    {
        var options = new VideoOptions();
        var renderer = new FinalRenderer(options, new Ffmpeg(options));
        Shot[] shots = [new(0, 0, 0, 90, ShotEffect.KenBurns, 0, false), new(1, 0, 90, 180, ShotEffect.KenBurns, 0, false)];
        var args = renderer.Arguments(shots, ["a.mp4", "b.mp4"], "voice.wav", 6, 0, "out.mp4", "watermark.png");
        Assert.Equal(Path.GetFullPath("watermark.png"), args[args.ToList().LastIndexOf("-i") + 1]);
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains($"[3:v]format=rgba,colorchannelmixer=aa={ShotRenderer.Num(CaptionBuilder.WatermarkOpacity)}[emblem]", graph);
        Assert.Contains($"[base][emblem]overlay=x={Layout.WatermarkLeft}:y={Layout.EmblemTop}", graph);
        Assert.True(graph.IndexOf("overlay=x=", StringComparison.Ordinal) < graph.IndexOf("ass=captions.ass", StringComparison.Ordinal));
        Assert.Contains("[2:a]", graph); // the voice keeps its input number

        var plain = renderer.Arguments(shots, ["a.mp4", "b.mp4"], "voice.wav", 6, 0, "out.mp4");
        Assert.DoesNotContain("[emblem]", plain[plain.ToList().IndexOf("-filter_complex") + 1]);
    }

    [Theory]
    [InlineData(60, 4500)]
    [InlineData(150, 2208)]
    [InlineData(175, 1865)]
    public void The_bitrate_cap_keeps_a_long_video_under_the_telegram_limit(double seconds, int expectedKbps)
    {
        var options = new VideoOptions();
        var renderer = new FinalRenderer(options, new Ffmpeg(options));
        Shot[] shots = [new(0, 0, 0, 90, ShotEffect.KenBurns, 0, false)];

        var args = renderer.Arguments(shots, ["a.mp4"], "voice.wav", seconds, 0, "out.mp4").ToList();

        Assert.Equal($"{expectedKbps}k", args[args.IndexOf("-maxrate") + 1]);
        // Video at the cap plus the audio, for the whole length, stays under the file budget.
        Assert.True((expectedKbps + FinalRenderer.AudioKbps) * seconds / 8000 <= options.MaxFileMegabytes);
    }

    /// <summary>
    /// The widest real chunks and a 21-letter word, rendered by libass with the repository's fonts: every chunk stays
    /// inside the caption area (centered, at most <see cref="Layout.CaptionMaxWidth"/> plus the outline), clear of the
    /// right-hand buttons. Also checks libass found Roboto Serif by name in fontsdir instead of falling back.
    /// </summary>
    [Fact]
    public async Task LongCaptionsStayInsideTheSafeAreaAsLibassDrawsThem()
    {
        using var temp = new TempFolder();
        var fonts = Path.Combine(temp.Path, "fonts");
        Directory.CreateDirectory(fonts);
        var video = new VideoOptions();
        foreach (var file in new[] { video.FontFile, video.StampFontFile })
            File.Copy(Path.Combine(TestPaths.RepositoryRoot, file), Path.Combine(fonts, Path.GetFileName(file)));

        var timings = Words("Os", "meteorologistas", "desembarcaram", "nas", "praias.", "O", "general", "Eisenhower", "decidiu.",
            "Inconstitucionalmente", "quarenta", "e", "quatro,", "Montgomery", "Rommel", "Normandia.");
        var style = new StyleOptions { YearCard = false, WordPop = false };
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "captions.ass"),
            CaptionBuilder.Build(timings, timings.Duration, video.FontName, video.StampFontName, "", "", 0, style, emblem: true));

        const int top = Layout.CaptionY - 110, rows = 220;
        var limit = Layout.CaptionMaxWidth / 2 + 8 + 2; // half the widest line, the outline, rounding
        var chunks = CaptionBuilder.Chunk(timings.Words);
        Assert.Contains(chunks, c => c.Words.Count == 1 && c.Words[0].Text == "Inconstitucionalmente");
        foreach (var chunk in chunks)
        {
            var at = ((chunk.Start + chunk.End) / 2).ToString("0.###", CultureInfo.InvariantCulture);
            var raw = Path.Combine(temp.Path, "frame.gray");
            var result = await ProcessRunner.RunCheckedAsync("ffmpeg",
                ["-y", "-hide_banner", "-loglevel", "verbose", "-f", "lavfi", "-i", $"color=black:s={Layout.Width}x{Layout.Height}:r=10:d={ShotRenderer.Num(timings.Duration)}",
                 "-ss", at, "-vf", $"ass=captions.ass:fontsdir=fonts,crop={Layout.Width}:{rows}:0:{top},format=gray", "-frames:v", "1",
                 "-f", "rawvideo", raw], temp.Path, TimeSpan.FromSeconds(60), CancellationToken.None);
            Assert.Contains($"fontselect: ({Black}, 400, 0) -> RobotoSerif-Black", result.StandardError);
            Assert.Contains($"fontselect: ({ExtraBold}, 400, 0) -> RobotoSerif-ExtraBold", result.StandardError);

            var pixels = await File.ReadAllBytesAsync(raw);
            Assert.Equal(Layout.Width * rows, pixels.Length);
            var lit = Enumerable.Range(0, Layout.Width).Where(x => Enumerable.Range(0, rows).Any(y => pixels[y * Layout.Width + x] > 40)).ToList();
            var text = string.Join(' ', chunk.Words.Select(w => w.Text));
            Assert.True(lit.Count > 0, $"nothing drawn for \"{text}\"");
            Assert.True(lit[0] >= Layout.Width / 2 - limit && lit[^1] <= Layout.Width / 2 + limit,
                $"\"{text}\" spans x {lit[0]} to {lit[^1]}, outside {Layout.Width / 2 - limit} to {Layout.Width / 2 + limit}");
        }
    }
}
