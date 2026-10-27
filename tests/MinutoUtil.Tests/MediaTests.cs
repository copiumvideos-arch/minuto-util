using System.Net;
using System.Text.Json;
using MinutoUtil.Core;
using MinutoUtil.Core.Media;
using MinutoUtil.Core.Scripts;
using MinutoUtil.Core.Video;
using MinutoUtil.Core.Voice;

namespace MinutoUtil.Tests;

public sealed class MediaTests
{
    [Theory]
    [InlineData("Public domain", null, "Public domain")]
    [InlineData("PD-USGov-Military-Army", null, "Public domain")]
    [InlineData("CC0", null, "CC0")]
    [InlineData("CC BY 4.0", null, "CC BY 4.0")]
    [InlineData("CC BY-SA 3.0", null, "CC BY-SA 3.0")]
    [InlineData("CC BY-SA 2.0 de", null, "CC BY-SA 2.0")]
    [InlineData("CC BY-NC 2.0", null, null)]
    [InlineData("CC BY-NC-SA 4.0", null, null)]
    [InlineData("CC BY-ND 4.0", null, null)]
    [InlineData("GFDL", null, null)]
    [InlineData("All rights reserved", null, null)]
    [InlineData(null, "http://creativecommons.org/publicdomain/zero/1.0/", "CC0")]
    [InlineData(null, "https://creativecommons.org/licenses/by-sa/4.0/", "CC BY-SA 4.0")]
    [InlineData(null, "https://creativecommons.org/licenses/by-nc/4.0/", null)]
    public void OnlySafeLicensesPass(string? name, string? url, string? expected) =>
        Assert.Equal(expected, LicensePolicy.Classify(name, url));

    [Fact]
    public void ParsesCommonsSearchResultsInSearchOrderWithCredits()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestPaths.Fixture("commons-search.json")));
        var items = CommonsSource.Parse(doc.RootElement, "Omaha Beach 1944 landing", MediaKind.Image);

        Assert.NotEmpty(items);
        Assert.All(items, i =>
        {
            Assert.StartsWith("commons:", i.Id);
            Assert.NotNull(LicensePolicy.Classify(i.License));
            Assert.False(string.IsNullOrWhiteSpace(i.Author));
            Assert.StartsWith("https://commons.wikimedia.org/wiki/File:", i.SourceUrl);
            Assert.StartsWith("https://", i.DownloadUrl);
            Assert.DoesNotContain("<", i.Author);
        });
        // Search order (the "index" of each page), not the order the pages come back in.
        Assert.StartsWith("SOMECA 511", items[0].Title);
        Assert.Equal("CC BY-SA 4.0", items[0].License);
        Assert.StartsWith("Infantry landing craft approach Omaha Beach", items[1].Title);
        Assert.Contains(items, i => i.Year == 1944);
    }

    [Theory]
    [InlineData("https://upload.wikimedia.org/wikipedia/commons/c/c8/Erwin_Rommel.jpg?utm_source=x", 1096,
        "https://upload.wikimedia.org/wikipedia/commons/thumb/c/c8/Erwin_Rommel.jpg/960px-Erwin_Rommel.jpg")]
    [InlineData("https://upload.wikimedia.org/wikipedia/commons/a/ab/Map.png", 2500,
        "https://upload.wikimedia.org/wikipedia/commons/thumb/a/ab/Map.png/1920px-Map.png")]
    [InlineData("https://upload.wikimedia.org/wikipedia/commons/a/ab/Tiny.jpg", 800, null)]
    public void NeverDownloadsAnOriginalOnlyAStandardThumbnail(string original, int width, string? expected) =>
        Assert.Equal(expected, CommonsSource.StandardThumbnail(original, width));

    [Fact]
    public void ParsesInternetArchiveNationalArchivesFootage()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestPaths.Fixture("archive-metadata.json")));
        var item = InternetArchiveSource.ParseMetadata(doc.RootElement, "111-adc-1489", "INVASION, OMAHA BEACH, NORMANDY, FRANCE",
            "National Archives", fromArchives: true, "Omaha Beach")!;

        Assert.Equal(MediaKind.Video, item.Kind);
        Assert.Equal("https://archive.org/download/111-adc-1489/111-adc-1489.mp4", item.DownloadUrl);
        Assert.Equal(1920, item.Width);
        Assert.True(item.DurationSeconds > 600);
        Assert.Equal("Public domain (US government work)", item.License);
        Assert.Contains("catalog.archives.gov/id/15296", item.SourceUrl);
        Assert.True(MediaDownloader.IsAllowedFootageUrl(item.DownloadUrl));
    }

    [Theory]
    [InlineData("https://archive.org/download/x/x.mp4", true)]
    [InlineData("https://ia800200.us.archive.org/x.mp4", true)]
    [InlineData("https://upload.wikimedia.org/wikipedia/commons/a/ab/x.webm", true)]
    [InlineData("http://archive.org/download/x/x.mp4", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("https://archive.org.evil.example/x.mp4", false)]
    [InlineData("concat:a|b", false)]
    public void FFmpegOnlyReadsFootageOverHttpsFromKnownHosts(string url, bool allowed) =>
        Assert.Equal(allowed, MediaDownloader.IsAllowedFootageUrl(url));

    [Fact]
    public void GraphicTitlesAreBlockedAndOldPhotosRankHigher()
    {
        var options = new MediaOptions();
        Assert.True(MediaScoring.IsBlocked("Dead soldiers on Omaha Beach", options.BlockedWords));
        Assert.True(MediaScoring.IsBlocked("Soldados mortos na praia", options.BlockedWords));
        Assert.False(MediaScoring.IsBlocked("Troops wade ashore at Omaha Beach", options.BlockedWords));

        MediaItem Item(string title, int? year) => new()
        {
            Id = title, Kind = MediaKind.Image, Source = "t", Title = title, Author = "a", License = "Public domain",
            SourceUrl = "s", DownloadUrl = "d", Width = 3000, Height = 2000, Year = year,
        };
        var period = MediaScoring.Score(Item("Landing craft 1944", 1944), 3, options.PenalizedWords, 1944);
        var memorial = MediaScoring.Score(Item("Omaha Beach memorial", 2014), 0, options.PenalizedWords, 1944);
        Assert.True(period > memorial);
    }

    [Fact]
    public void RejectsBytesThatAreNotPictures()
    {
        Assert.Equal(".jpg", MediaDownloader.ImageExtension([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal(".png", MediaDownloader.ImageExtension([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]));
        Assert.Null(MediaDownloader.ImageExtension("<!DOCTYPE html><html>"u8));
        Assert.Null(MediaDownloader.ImageExtension("MZ\u0090\0\u0003\0\0\0\u0004\0\0\0"u8));
    }

    [Fact]
    public async Task DownloadsAValidPictureAndSkipsAnHtmlErrorPage()
    {
        using var temp = new TempFolder();
        var picture = Path.Combine(temp.Path, "source.jpg");
        await Generated.ImageAsync(picture, 1600, 1200);
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("good.jpg")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(picture)) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>" + new string('x', 60000) + "</html>") });
        var options = new MediaOptions { MinImageBytes = 1000 };
        var downloader = new MediaDownloader(new HttpClient(handler), new Ffmpeg(new VideoOptions()), options) { DownloadInterval = TimeSpan.Zero };
        MediaItem Item(string url) => new()
        {
            Id = url, Kind = MediaKind.Image, Source = "t", Title = "t", Author = "a", License = "Public domain", SourceUrl = url, DownloadUrl = url,
        };

        var good = await downloader.DownloadImageAsync(Item("https://example.org/good.jpg"), Path.Combine(temp.Path, "media"), "a", CancellationToken.None);
        var bad = await downloader.DownloadImageAsync(Item("https://example.org/bad.jpg"), Path.Combine(temp.Path, "media"), "b", CancellationToken.None);

        Assert.NotNull(good);
        Assert.Equal(1600, good!.Width);
        Assert.NotNull(good.Hash);
        Assert.Null(bad);
        Assert.Single(Directory.GetFiles(Path.Combine(temp.Path, "media")));
    }

    /// <summary>A search source that finds nothing, like the archives for a 1996 subject.</summary>
    private sealed class EmptySource : IMediaSource
    {
        public string Name => "empty";
        public Task<IReadOnlyList<MediaItem>> SearchAsync(string query, MediaKind kind, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MediaItem>>([]);
    }

    private sealed class ArticlePictures(params MediaItem[] items) : IArticleImageSource
    {
        public List<(string Language, string Title)> Asked { get; } = [];
        public Task<IReadOnlyList<MediaItem>> ArticleImagesAsync(string language, string title, int limit, CancellationToken ct)
        {
            Asked.Add((language, title));
            return Task.FromResult<IReadOnlyList<MediaItem>>(language == "pt" ? items : []);
        }
    }

    [Fact]
    public async Task SectionsTheSearchesLeaveShortGetTheArticlesOwnPictures()
    {
        using var temp = new TempFolder();
        string[] patterns = ["testsrc2", "smptebars", "mandelbrot", "rgbtestsrc"];
        foreach (var p in patterns) await Generated.ImageAsync(Path.Combine(temp.Path, $"{p}.jpg"), 1600, 1200, p);
        var handler = new FakeHandler((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(temp.Path, Path.GetFileName(request.RequestUri!.AbsolutePath)))) });
        var options = new MediaOptions { MinImageBytes = 1000 };
        var downloader = new MediaDownloader(new HttpClient(handler), new Ffmpeg(new VideoOptions()), options) { DownloadInterval = TimeSpan.Zero };
        MediaItem Picture(string pattern) => new()
        {
            Id = $"commons:{pattern}.jpg", Kind = MediaKind.Image, Source = "Wikimedia Commons", Title = $"Urna eletrônica {pattern}",
            Author = "TSE", License = "CC BY 3.0", SourceUrl = $"https://commons.wikimedia.org/wiki/File:{pattern}.jpg",
            DownloadUrl = $"https://upload.wikimedia.org/{pattern}.jpg", Width = 1600, Height = 1200,
        };
        var articles = new ArticlePictures([.. patterns.Select(Picture)]);
        var gatherer = new MediaGatherer([new EmptySource()], [], downloader, null!, options, 1996, articles);
        var section = new ScriptSection("Em 1996 o Brasil adotou a urna eletrônica.", ["voting machine"], ["urna"], "q");
        var script = new VideoScript("Urna", "Por que só o Brasil?", "1996", "Brasil", [section, section], "d", ["urna"]);

        var manifest = await gatherer.GatherAsync(script, [1, 2, 1], 0, ["Electronic voting in Brazil"], Path.Combine(temp.Path, "media"),
            CancellationToken.None, [("en", "Electronic voting in Brazil"), ("pt", "Urna eletrônica (Brasil)")]);

        // Four different photos for four shots: no picture shown twice, every section covered, Portuguese article asked first.
        Assert.Equal(4, manifest.Images.Count);
        Assert.Equal(4, manifest.Images.Select(i => i.Id).Distinct().Count());
        Assert.Equal([0, 1, 1, 2], manifest.Images.Select(i => i.Section).Order());
        Assert.Equal(("pt", "Urna eletrônica (Brasil)"), articles.Asked[0]);
    }

    [Fact]
    public void ClipJudgeKeepsTheLongestStretchWithoutACut()
    {
        var stats = new List<ClipExtractor.FrameStat>();
        for (var i = 0; i < 180; i++)
            stats.Add(new ClipExtractor.FrameStat(110, 150, i == 30 ? 60 : 3)); // a hard cut at frame 30
        var (offset, score) = ClipExtractor.Judge(stats, 30, 4.5);
        Assert.Equal(1.0, offset, 2);
        Assert.True(score > 0.5);

        var black = Enumerable.Repeat(new ClipExtractor.FrameStat(10, 20, 1), 180).ToList();
        Assert.Equal(0, ClipExtractor.Judge(black, 30, 4.5).Score);
    }
}

public sealed class PlannerAndCaptionTests
{
    /// <summary>Hook of 3 s, then sections of 9 s and 4 s, words every 0.5 s with a comma now and then.</summary>
    private static SpeechTimings Timings()
    {
        var words = new List<SpokenWord>();
        var sections = new List<SectionTime>();
        double t = 0;
        var lengths = new[] { 3.0, 9.0, 4.0 };
        for (var s = 0; s < lengths.Length; s++)
        {
            var start = t;
            var count = (int)(lengths[s] / 0.5);
            for (var w = 0; w < count; w++)
            {
                var text = w % 5 == 4 ? $"palavra{w}," : $"palavra{w}";
                words.Add(new SpokenWord(text, t, t + 0.4, s));
                t += 0.5;
            }
            sections.Add(new SectionTime(start, t - 0.1));
            t += 0.4;
        }
        return new SpeechTimings(t, sections, words, []);
    }

    [Fact]
    public void PlansShotsOnSectionBoundariesAndSplitsLongSections()
    {
        var timings = Timings();
        var options = new VideoOptions();
        var duration = Math.Ceiling((timings.Duration + 0.6) * 30) / 30;
        var shots = ShotPlanner.Plan(timings, duration, options, clipSeconds: 4.5);

        Assert.Equal(ShotEffect.Clip, shots[0].Effect);
        // The 9 s section gets two shots, the others one.
        Assert.Equal([0, 1, 1, 2], shots.Select(s => s.Section));
        Assert.Equal(0, shots[0].Start);
        Assert.Equal((int)Math.Round(duration * 30), shots[^1].End);
        for (var i = 1; i < shots.Count; i++) Assert.Equal(shots[i - 1].End, shots[i].Start);
        // Stills alternate parallax and Ken Burns.
        Assert.Equal([ShotEffect.Parallax, ShotEffect.KenBurns, ShotEffect.Parallax], shots.Skip(1).Select(s => s.Effect));
        // The split falls between two words, right after a comma.
        var cut = shots[2].Start / 30.0;
        var before = timings.Words.Last(w => w.End <= cut + 0.01);
        Assert.EndsWith(",", before.Text);

        // Segment lengths: joined with cross-fades, they add up to the whole video.
        var fade = 10;
        var total = Enumerable.Range(0, shots.Count).Sum(i => ShotPlanner.SegmentFrames(shots, i, fade)) - (shots.Count - 1) * fade;
        Assert.Equal(shots[^1].End, total);
    }

    [Fact]
    public void TheMapShotOpensTheFirstSectionAfterTheHook()
    {
        var timings = Timings();
        var shots = ShotPlanner.Plan(timings, timings.Duration + 0.6, new VideoOptions(), clipSeconds: 4.5, mapSeconds: 2.5);
        var map = Assert.Single(shots, s => s.Effect == ShotEffect.Map);
        Assert.Equal(1, map.Index);
        Assert.Equal(1, map.Section);
        Assert.InRange(map.Frames, 74, 76); // 2.5 s, give or take the rounding of each cut to a frame
        Assert.Equal(ShotEffect.Parallax, shots[2].Effect);
        Assert.Equal(1, shots[2].Section);
    }

    [Fact]
    public void WithoutAClipTheHookIsAStill()
    {
        var timings = Timings();
        var shots = ShotPlanner.Plan(timings, timings.Duration + 0.6, new VideoOptions(), clipSeconds: 0);
        Assert.Equal(ShotEffect.Parallax, shots[0].Effect);
        Assert.DoesNotContain(shots, s => s.Effect == ShotEffect.Clip);
    }

    [Fact]
    public void CaptionsShowTwoToFourWordsAndNeverCrossASection()
    {
        var timings = Timings();
        var chunks = CaptionBuilder.Chunk(timings.Words);

        Assert.All(chunks, c => Assert.InRange(c.Words.Count, 1, 4));
        Assert.True(chunks.Count(c => c.Words.Count == 1) <= 1);
        Assert.All(chunks, c => Assert.Single(c.Words.Select(w => w.Section).Distinct()));
        for (var i = 1; i < chunks.Count; i++) Assert.True(chunks[i].Start >= chunks[i - 1].End - 0.001);
        Assert.Equal(timings.Words.Count, chunks.Sum(c => c.Words.Count));
    }

    [Fact]
    public void StyleSwitchesTurnEachCaptionLayerOnAndOff()
    {
        var on = CaptionBuilder.Build(Timings(), 18, "Roboto Serif Black", "Roboto Serif ExtraBold", "6 de junho de 1944", "Normandia, França", 3.5, new StyleOptions());
        Assert.Contains("Minuto Útil", on);
        Assert.Contains("NORMANDIA, FRANÇA", on);
        Assert.Contains("}44", on);
        Assert.Contains(@"\fscy125", on);
        Assert.DoesNotContain(",Stamp,", on);

        var off = CaptionBuilder.Build(Timings(), 18, "Roboto Serif Black", "Roboto Serif ExtraBold", "6 de junho de 1944", "Normandia, França", 3.5,
            new StyleOptions { YearCard = false, Watermark = false, WordPop = false, KeyWords = false });
        Assert.DoesNotContain("Minuto Útil", off);
        Assert.DoesNotContain(@"\fscy125", off);
        Assert.Contains(",Stamp,", off);
    }

    [Fact]
    public void KeyWordsAreNumbersMonthsAndNamesNotSentenceStarts()
    {
        SpokenWord W(string text, int i) => new(text, i, i + 0.5, 0);
        var words = new[] { "Em", "seis", "de", "junho,", "Eisenhower", "decidiu", "com", "mil", "e", "quinhentos", "homens.", "Ele", "voltou", "em", "1944." }
            .Select(W).ToList();
        var keys = CaptionBuilder.KeyWords(words).Select(w => w.Text).ToList();
        Assert.Equal(["seis", "junho,", "Eisenhower", "mil", "e", "quinhentos", "1944."], keys);
    }

    [Fact]
    public void PictureChainFollowsTheSwitches()
    {
        var all = new FinalRenderer(new VideoOptions(), new Ffmpeg(new VideoOptions())).PictureChain(10);
        Assert.Contains("unsharp=5:5:0.7", all);
        Assert.Contains("vignette=angle=0.6", all);
        Assert.Contains("noise=c0s=7", all);
        Assert.Contains("eq=contrast=1.12:saturation=1.18", all);
        // Sharpening comes before the grain, so the grain is not sharpened.
        Assert.True(all.IndexOf("unsharp", StringComparison.Ordinal) < all.IndexOf("noise", StringComparison.Ordinal));

        var none = new VideoOptions { Style = new StyleOptions { Vintage = false, Sharpen = 0, Denoise = false, Contrast = 1, Saturation = 1 } };
        Assert.Equal("format=yuv420p", new FinalRenderer(none, new Ffmpeg(none)).PictureChain(10));
    }

    [Fact]
    public void BuildsTheAssDocumentWithHighlightAndDateStamp()
    {
        var ass = CaptionBuilder.Build(Timings(), 18, "Roboto Serif Black", "Roboto Serif ExtraBold", "6 de junho de 1944", "Normandia, França", 3.5);
        Assert.Contains("6 DE JUNHO DE 1944", ass);
        Assert.Contains("NORMANDIA, FRANÇA", ass);
        Assert.Contains("\\c&H000A01DC&", ass);
        Assert.Contains("\\pos(540,1260)", ass);
        Assert.Contains("Dialogue: 1,0:00:00.15,0:00:03.50,Stamp", ass);
    }
}

public sealed class VoiceTests
{
    [Fact]
    public async Task SendsTheDocumentedRequestAndCachesEachTake()
    {
        using var temp = new TempFolder();
        var wav = Path.Combine(temp.Path, "tone.wav");
        await Generated.ToneAsync(wav, 1);
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(wav)) });
        var usage = new RunUsage();
        var voice = new SmallestVoice(new HttpClient(handler), "secret-key", new SmallestOptions(), usage);

        await voice.SpeakAsync(["Olá.", "Segunda seção."], Path.Combine(temp.Path, "takes"), CancellationToken.None);
        await voice.SpeakAsync(["Olá.", "Seção mudada."], Path.Combine(temp.Path, "takes"), CancellationToken.None);

        // The unchanged first take is reused; only the changed one is spoken again.
        Assert.Equal(3, handler.Requests.Count);
        var (request, body) = handler.Requests[0];
        Assert.Equal("https://api.smallest.ai/waves/v1/tts", request.RequestUri!.ToString());
        Assert.Equal("Bearer secret-key", request.Headers.Authorization!.ToString());
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("Olá.", json.GetProperty("text").GetString());
        Assert.Equal("thiago", json.GetProperty("voice_id").GetString());
        Assert.Equal("lightning_v3.1_pro", json.GetProperty("model").GetString());
        Assert.Equal("pt", json.GetProperty("language").GetString());
        Assert.Equal(24000, json.GetProperty("sample_rate").GetInt32());
        Assert.Equal(1.0, json.GetProperty("speed").GetDouble());
        Assert.Equal("wav", json.GetProperty("output_format").GetString());
        Assert.Equal("Olá.Segunda seção.Seção mudada.".Length, usage.SmallestCharacters);
    }

    [Fact]
    public async Task OutOfCreditsFailsWithoutLeakingTheKey()
    {
        using var temp = new TempFolder();
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.PaymentRequired, """{"error":"Insufficient credits for key secret-key"}"""));
        var voice = new SmallestVoice(new HttpClient(handler), "secret-key", new SmallestOptions(), new RunUsage());

        var error = await Assert.ThrowsAsync<VoiceException>(() => voice.SpeakAsync(["Olá."], temp.Path, CancellationToken.None));

        Assert.Contains("402", error.Message);
        Assert.DoesNotContain("secret-key", error.Message);
    }

    [Fact]
    public async Task ARefusedAnswerThatIsNotAudioIsAnError()
    {
        using var temp = new TempFolder();
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, """{"status":"queued"}"""));
        var voice = new SmallestVoice(new HttpClient(handler), "k", new SmallestOptions(), new RunUsage());
        await Assert.ThrowsAsync<VoiceException>(() => voice.SpeakAsync(["Olá."], temp.Path, CancellationToken.None));
    }
}
