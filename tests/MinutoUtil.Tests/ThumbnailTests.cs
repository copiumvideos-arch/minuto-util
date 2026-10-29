using MinutoUtil.Core;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Tests;

public sealed class ThumbnailTests
{
    [Fact]
    public void Words_between_asterisks_are_red_and_everything_is_uppercase()
    {
        Assert.Equal(
            [new("O", false), new("BILHETE", false), new("QUE", false), new("NINGUÉM", true), new("LEU", false)],
            Thumbnail.Words("O bilhete que *ninguém* leu"));
        // Several words in one pair, punctuation after the closing asterisk, and a lone asterisk dropped.
        Assert.Equal(
            [new("OS", false), new("TRÊS", true), new("NAVIOS,", true), new("E", false), new("DEPOIS", false)],
            Thumbnail.Words("Os *três navios*, * e depois"));
        Assert.All(Thumbnail.Words("Sem destaque nenhum"), w => Assert.False(w.Highlight));
    }

    [Theory]
    [InlineData("6 de junho de 1944", "1944")]
    [InlineData("15 de março de 44 a.C.", "44 A.C.")]
    [InlineData("por volta de 1274 a.C.", "1274 A.C.")]
    [InlineData("Século dezesseis", null)]
    public void The_year_line_comes_from_the_date_stamp(string stamp, string? expected) =>
        Assert.Equal(expected, Thumbnail.YearLabel(stamp));

    [Fact]
    public void Short_text_is_set_big_and_every_line_fits_the_frame()
    {
        var layout = Thumbnail.Arrange(Thumbnail.Words("O faraó *apagado*"), "1336 A.C.");

        Assert.Equal(Thumbnail.MaxFontSize, layout.FontSize);
        Assert.InRange(layout.Lines.Count, 1, 2);
        Assert.All(layout.Lines, l => Assert.InRange(Thumbnail.LineWidth(l, layout.FontSize), 1, Thumbnail.MaxLineWidth));
    }

    [Fact]
    public void Lines_are_balanced_so_no_word_is_left_alone_at_the_end()
    {
        var layout = Thumbnail.Arrange(Thumbnail.Words("A previsão que *mudou* o Dia D"), "1944");

        Assert.Equal(["A PREVISÃO", "QUE MUDOU", "O DIA D"], layout.Lines.Select(l => string.Join(' ', l.Select(w => w.Text))));
        Assert.All(layout.Lines, l => Assert.True(l.Count > 1));
    }

    [Fact]
    public void A_long_title_as_the_fallback_gets_smaller_and_more_lines_but_stays_inside()
    {
        var words = Thumbnail.Words("Como a Previsão do Tempo Mudou o Rumo da Segunda Guerra Mundial na Normandia em Junho");
        var layout = Thumbnail.Arrange(words, null);

        Assert.True(layout.FontSize < Thumbnail.MaxFontSize);
        Assert.Equal(words, layout.Lines.SelectMany(l => l));
        Assert.All(layout.Lines, l => Assert.True(Thumbnail.LineWidth(l, layout.FontSize) <= Thumbnail.MaxLineWidth));
    }

    [Fact]
    public void The_ass_file_uses_the_brand_colors_and_keeps_the_text_in_the_tiktok_grid_crop()
    {
        var style = new StyleOptions { WatermarkText = "Minuto Útil" };
        var layout = Thumbnail.Arrange(Thumbnail.Words("O bilhete que *ninguém* leu"), "44 A.C.");

        var ass = Thumbnail.Ass(layout, "Roboto Serif Black", "Roboto Serif ExtraBold", style, emblem: true);

        var red = CaptionBuilder.AssColor(style.HighlightColor);
        var cream = CaptionBuilder.AssColor(style.TextColor);
        Assert.Contains($@"{{\c{red}}}NINGUÉM", ass);
        Assert.Contains($@"{{\c{cream}}}BILHETE", ass);
        Assert.Contains("}44 A.C.", ass);
        Assert.Contains("Minuto Útil", ass);
        Assert.DoesNotContain("yellow", ass, StringComparison.OrdinalIgnoreCase);
        // TikTok's profile grid crops covers to 3:4, y 240 to 1680: every line's middle is well inside.
        var ys = System.Text.RegularExpressions.Regex.Matches(ass, @"\\pos\(540,(\d+)\)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.NotEmpty(ys);
        Assert.All(ys, y => Assert.InRange(y, 240 + 150, 1680 - 150));
    }
}

public sealed class CoverPainterTests
{
    private const string Account = "b77e134015ef6359c129cf8cfe3bb38a";

    [Fact]
    public async Task A_refused_painting_is_one_request_and_an_error_without_the_token()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(System.Net.HttpStatusCode.TooManyRequests,
            """{"success":false,"errors":[{"code":4006,"message":"you have used up your daily free allocation of 10,000 neurons"}]}"""));
        var usage = new RunUsage();
        var painter = new CloudflareCoverPainter(new HttpClient(handler), "cf_secret_token", Account, new CloudflareAiOptions(), usage);

        var error = await Assert.ThrowsAsync<CoverArtException>(() => painter.PaintAsync("A harbor at night", CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Equal(1, usage.CoverImages);
        Assert.Contains("429", error.Message);
        Assert.DoesNotContain("cf_secret_token", error.Message);
        Assert.DoesNotContain("cf_secret_token", handler.Requests[0].Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_picture_is_refused()
    {
        // An HTML page in base64 decodes fine but is not an image, so FFmpeg never sees it.
        var html = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("<html>" + new string('x', 4000) + "</html>"));
        var handler = new FakeHandler((_, _) => FakeHandler.Json(System.Net.HttpStatusCode.OK, $$$"""{"result":{"image":"{{{html}}}"}}"""));
        var painter = new CloudflareCoverPainter(new HttpClient(handler), "t", Account, new CloudflareAiOptions());

        await Assert.ThrowsAsync<CoverArtException>(() => painter.PaintAsync("A harbor at night", CancellationToken.None));
        Assert.Null(CloudflareCoverPainter.Decode("""{"result":{"image":"not base64!"}}"""));
        Assert.Null(CloudflareCoverPainter.Decode("<html>"));
        await Assert.ThrowsAsync<CoverArtException>(() => painter.PaintAsync(" ", CancellationToken.None));
    }

    [Fact]
    public void Pictures_are_told_apart_by_their_first_bytes_and_a_data_uri_is_read()
    {
        byte[] Padded(params byte[] head) => [.. head, .. new byte[2000]];
        Assert.Equal(".jpg", CloudflareCoverPainter.ExtensionOf(Padded(0xFF, 0xD8, 0xFF, 0xE0)));
        Assert.Equal(".png", CloudflareCoverPainter.ExtensionOf(Padded(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)));
        Assert.Equal(".webp", CloudflareCoverPainter.ExtensionOf(Padded(0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50)));
        Assert.Null(CloudflareCoverPainter.ExtensionOf([0xFF, 0xD8, 0xFF]));
        Assert.Equal([1, 2, 3], CloudflareCoverPainter.Decode("""{"image":"data:image/jpeg;base64,AQID"}"""));
    }

    [Theory]
    [InlineData("../evil", "@cf/black-forest-labs/flux-2-klein-4b")]
    [InlineData(Account, "@cf/x/y?token=1")]
    [InlineData(Account, "https://example.org/model")]
    public void The_account_and_model_must_look_right_before_they_go_in_the_url(string account, string model) =>
        Assert.Throws<CoverArtException>(() =>
            new CloudflareCoverPainter(new HttpClient(), "t", account, new CloudflareAiOptions { ImageModel = model }));

    [Fact]
    public void Every_prompt_starts_with_the_channel_style_so_the_covers_look_alike()
    {
        var prompt = CloudflareCoverPainter.Prompt(new CloudflareAiOptions(), "  A harbor at night ");
        Assert.StartsWith("Vintage oil painting", prompt);
        Assert.Contains("No text", prompt);
        Assert.EndsWith("Scene: A harbor at night", prompt);
    }
}
