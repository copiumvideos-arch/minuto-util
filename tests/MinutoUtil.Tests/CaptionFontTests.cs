using MinutoUtil.Core;
using MinutoUtil.Core.Video;
using MinutoUtil.Core.Voice;

namespace MinutoUtil.Tests;

/// <summary>Captions in each channel's font, and the watermark as the channel's name alone (Profissão Spoiler's look).</summary>
public sealed class CaptionFontTests
{
    [Fact]
    public void MeasuresEachFontWithItsOwnWidths()
    {
        // Montserrat Black is narrower than Roboto Serif Black at the same size (libass sizes both by their win metrics).
        var roboto = CaptionBuilder.CaptionWidth("DESEMBARCARAM NAS");
        var montserrat = CaptionBuilder.CaptionWidth("DESEMBARCARAM NAS", "Montserrat Black");
        Assert.InRange(montserrat / roboto, 0.8, 0.9);
        Assert.Throws<ArgumentException>(() => CaptionBuilder.CaptionWidth("A", "Comic Sans MS"));
    }

    [Fact]
    public void TheWatermarkCanBeTheChannelsNameAlone()
    {
        SpokenWord[] words = [new("Hollow", 0, 0.4, 0), new("Knight", 0.4, 0.9, 0)];
        var timings = new SpeechTimings(1, [new SectionTime(0, 1)], words, []);
        var style = new StyleOptions { YearCard = false, Vintage = false, WatermarkText = "Profissão Spoiler" };

        var ass = CaptionBuilder.Build(timings, 1.5, "Montserrat Black", "Montserrat ExtraBold", "", "", 0, style, icon: false);

        Assert.Contains($"\\pos({Layout.WatermarkLeft},{Layout.WatermarkTop + 22})\\alpha&H58&}}Profissão Spoiler", ass);
        Assert.DoesNotContain("\\p1", ass);
        Assert.Contains("Style: Caption,Montserrat Black,", ass);
        // No date stamp without an era stamp.
        Assert.DoesNotContain(",Stamp,", ass);
    }
}
