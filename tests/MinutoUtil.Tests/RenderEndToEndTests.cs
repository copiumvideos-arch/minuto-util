using System.Net;
using System.Text.Json;
using MinutoUtil.Core;
using MinutoUtil.Core.Media;
using MinutoUtil.Core.Scripts;
using MinutoUtil.Core.Sources;
using MinutoUtil.Core.Video;
using MinutoUtil.Core.Voice;

namespace MinutoUtil.Tests;

/// <summary>
/// The whole pipeline on real files with real FFmpeg and the real motion tool: only the network (Wikipedia, the
/// writer, Smallest.ai) and the models (Whisper, Depth Anything) are replaced. Smallest answers with generated tones,
/// the pictures are FFmpeg test patterns with generated depth maps, and the clip is a generated video.
/// Needs FFmpeg on PATH and the tools/ uv environment.
/// </summary>
public sealed class RenderEndToEndTests
{
    [Fact]
    public async Task RendersAFinishedShortFromCachedScriptAndMediaWithAFakeVoice()
    {
        using var temp = new TempFolder();
        var run = Path.Combine(temp.Path, "run");
        Directory.CreateDirectory(run);
        var root = TestPaths.RepositoryRoot;

        // Sources and script as a previous run saved them: no writer request is made.
        var script = ScriptWriter.Clean(JsonSerializer.Deserialize<VideoScript>(
            await File.ReadAllTextAsync(TestPaths.Fixture("script-answer.json")), Json.Options)!) with
        {
            Location = new ScriptLocation("Normandia", 49.4, -0.9),
            ThumbnailText = "A previsão que *mudou* o Dia D",
            ThumbnailScene = "A stormy sea at dawn, grey landing craft heading to a beach",
        };
        // A tiny stand-in for Natural Earth: two squares around the place, so the map tool needs no download.
        var mapData = Path.Combine(temp.Path, "naturalearth");
        Directory.CreateDirectory(mapData);
        await File.WriteAllTextAsync(Path.Combine(mapData, "ne_10m_admin_0_countries.geojson"), """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{},"geometry":{"type":"Polygon","coordinates":[[[-5,43],[8,43],[8,51],[-5,51],[-5,43]]]}},
              {"type":"Feature","properties":{},"geometry":{"type":"MultiPolygon","coordinates":[[[[-6,50.5],[2,50.5],[2,58],[-6,58],[-6,50.5]]]]}}]}
            """);
        await File.WriteAllTextAsync(Path.Combine(run, "script.json"), JsonSerializer.Serialize(script, Json.Options));
        SourceArticle[] sources = [new("pt", "Desembarques na Normandia", "https://pt.wikipedia.org/wiki/Desembarques_na_Normandia", "texto", 1)];
        await File.WriteAllTextAsync(Path.Combine(run, "sources.json"), JsonSerializer.Serialize(sources, Json.Options));

        // Smallest.ai replaced: each request gets a generated tone of a length that depends on the text.
        var toneFolder = Path.Combine(temp.Path, "tones");
        Directory.CreateDirectory(toneFolder);
        var handler = new FakeHandler((request, body) =>
        {
            var text = JsonDocument.Parse(body).RootElement.GetProperty("text").GetString()!;
            var tone = Path.Combine(toneFolder, $"{Guid.NewGuid():N}.wav");
            Generated.ToneAsync(tone, Math.Clamp(text.Length / 25.0, 1.2, 5.0), 300 + text.Length).GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(tone)) };
        });
        var usage = new RunUsage();
        var voiceOptions = new VoiceOptions { Listener = "none" };
        var smallest = new SmallestVoice(new HttpClient(handler), "test-key", voiceOptions.Smallest, usage);
        var tools = Path.Combine(root, "tools");
        var narrator = new Narrator(voiceOptions, tools, smallest, new KokoroVoice(tools, voiceOptions), usage);

        // Media as the gatherer would have left it: two pictures per section, depth maps next to them, and a clip.
        var media = Path.Combine(run, "media");
        Directory.CreateDirectory(media);
        var images = new List<MediaItem>();
        for (var section = 0; section < script.SpokenSections.Count; section++)
            for (var k = 0; k < 2; k++)
            {
                var path = Path.Combine(media, $"s{section}-{k}.jpg");
                await Generated.ImageAsync(path, 1600, 1200, k % 2 == 0 ? "testsrc2" : "smptehdbars");
                await Generated.DepthAsync(Path.ChangeExtension(path, ".depth.png"), 1600, 1200);
                images.Add(Item($"img-{section}-{k}", MediaKind.Image, path, section));
            }
        var clipPath = Path.Combine(media, "clip.mp4");
        await Generated.VideoAsync(clipPath, 6, 1920, 1080);
        var manifest = new MediaManifest(images, Item("clip", MediaKind.Video, clipPath, 0) with { Width = 1920, Height = 1080 });
        await manifest.SaveAsync(Path.Combine(media, "manifest.json"), CancellationToken.None);

        // Cloudflare Workers AI replaced: the painted cover is a generated portrait picture, sent back as base64 like the
        // real API, until paintFails is set.
        var painted = Path.Combine(temp.Path, "painted.jpg");
        await Generated.ImageAsync(painted, 864, 1536, "rgbtestsrc");
        var paintFails = false;
        var cloudflare = new FakeHandler((_, _) => paintFails
            ? FakeHandler.Json(HttpStatusCode.TooManyRequests, """{"success":false,"errors":[{"code":4006,"message":"daily free allocation used up"}]}""")
            : FakeHandler.Json(HttpStatusCode.OK, $$"""{"result":{"image":"{{Convert.ToBase64String(File.ReadAllBytes(painted))}}"},"success":true}"""));

        // Every style layer on (the defaults) and the painted cover, with the map reading the stand-in data.
        var config = new AppConfig
        {
            Video = new VideoOptions
            {
                MotionSupersample = 1.0, Style = new StyleOptions { MapDataDirectory = mapData, MapSeconds = 1.5, ThumbnailPicture = "ai" },
            },
            Voice = voiceOptions,
        };
        var painter = new CloudflareCoverPainter(new HttpClient(cloudflare), "cf_ai_token", "b77e134015ef6359c129cf8cfe3bb38a", config.CloudflareAi, usage);
        var pipeline = new Pipeline(config, root, new WikipediaClient(new HttpClient(new FakeHandler((_, _) => throw new InvalidOperationException("no network")))),
            () => throw new InvalidOperationException("the cached script must be used"), narrator, NoGatherer(), painter);

        var result = await pipeline.RunAsync(run, CancellationToken.None);

        // One Smallest request per spoken section, and the characters counted.
        Assert.Equal(script.SpokenSections.Count, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer test-key", r.Request.Headers.Authorization!.ToString()));
        Assert.Equal(script.SpokenSections.Sum(s => SpokenText.SpellNumbers(s).Length), usage.SmallestCharacters);
        // The section with "6 de junho" reached the voice spelled out.
        Assert.Contains(handler.Requests, r => r.Body.Contains("seis de junho"));

        var probe = result.Probe;
        Assert.Equal("h264", probe.VideoCodec);
        Assert.Equal("aac", probe.AudioCodec);
        Assert.Equal(1080, probe.Width);
        Assert.Equal(1920, probe.Height);
        Assert.Equal(30, probe.Fps, 1);
        Assert.InRange(probe.Duration, result.Duration - 0.1, result.Duration + 0.1);
        Assert.NotNull(result.Lufs);
        Assert.InRange(result.Lufs!.Value, -18, -14);

        foreach (var file in new[] { "video.mp4", "script.json", "metadata.json", "timings.json", "contact-sheet.png", "captions.ass", Thumbnail.FileName })
            Assert.True(File.Exists(Path.Combine(run, file)), file);

        var timings = JsonSerializer.Deserialize<TimingsFile>(await File.ReadAllTextAsync(Path.Combine(run, "timings.json")), Json.Options)!;
        Assert.Equal("clip", timings.Shots[0].Effect);
        Assert.Contains(timings.Shots, s => s.Effect == "parallax");
        Assert.Contains(timings.Shots, s => s.Effect == "kenburns");
        // The map opens the first section after the hook, for the configured length.
        var map = Assert.Single(timings.Shots, s => s.Effect == "map");
        Assert.Equal(1, map.Section);
        Assert.Equal(1.5, map.End - map.Start, 1);
        Assert.Equal(timings.Shots.Count, timings.Shots.Select(s => s.Media).Distinct().Count());
        var ass = await File.ReadAllTextAsync(Path.Combine(run, "captions.ass"));
        Assert.Contains("Minuto Útil", ass);
        // The emblem picture was found and overlaid, so the captions carry only its text, not the vector hourglass.
        Assert.Contains($@"\pos({Layout.WatermarkLeft + Layout.EmblemSize + 14},", ass);
        Assert.DoesNotContain("m 0 0 l 30 0", ass);
        Assert.Contains("}44", ass); // the year card's last value
        Assert.DoesNotContain(",Stamp,", ass); // the card replaces the plain date stamp
        Assert.Contains(@"\alpha&HFF&", ass); // words not yet spoken are hidden
        Assert.Equal(result.Duration, timings.Shots[^1].End, 2);

        // The cover: a 1080x1920 JPEG under YouTube's 2 MB, from the first still (not the clip), with the red word.
        var thumbnail = await new Ffmpeg(config.Video).ProbeAsync(Path.Combine(run, Thumbnail.FileName), CancellationToken.None);
        Assert.Equal((1080, 1920), (thumbnail.Width, thumbnail.Height));
        Assert.Equal("mjpeg", thumbnail.VideoCodec);
        Assert.InRange(new FileInfo(Path.Combine(run, Thumbnail.FileName)).Length, 20_000, Thumbnail.MaxBytes);
        var cover = await File.ReadAllTextAsync(Path.Combine(run, "thumbnail.ass"));
        Assert.Contains($@"{{\c{CaptionBuilder.AssColor(config.Video.Style.HighlightColor)}}}MUDOU", cover);
        Assert.Contains("}1944", cover);

        // The picture behind it was painted once: the channel's style and the script's scene, the size, the token in
        // the header and not the URL.
        var paint = Assert.Single(cloudflare.Requests);
        Assert.Equal("https://api.cloudflare.com/client/v4/accounts/b77e134015ef6359c129cf8cfe3bb38a/ai/run/@cf/black-forest-labs/flux-2-klein-4b",
            paint.Request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer cf_ai_token", paint.Request.Headers.Authorization!.ToString());
        Assert.Matches(FormField("prompt", "Vintage oil painting"), paint.Body);
        Assert.Matches(FormField("height", "1536"), paint.Body);
        Assert.Contains("Scene: A stormy sea at dawn, grey landing craft heading to a beach", paint.Body);
        Assert.Matches(FormField("width", "864"), paint.Body);
        Assert.Equal(1, usage.CoverImages);
        Assert.Equal(await File.ReadAllBytesAsync(painted), await File.ReadAllBytesAsync(Path.Combine(run, "cover-art.jpg")));
        var paintedThumbnail = await File.ReadAllBytesAsync(Path.Combine(run, Thumbnail.FileName));

        // The video's first frame is the cover, for TikTok's cover picker; the second is the video again.
        Assert.InRange(await SimilarityAsync(run, 0), 0.9, 1.0);
        Assert.InRange(await SimilarityAsync(run, 1), 0.0, 0.6);

        var metadata = JsonSerializer.Deserialize<VideoMetadata>(await File.ReadAllTextAsync(Path.Combine(run, "metadata.json")), Json.Options)!;
        Assert.Contains("Créditos:", metadata.Description);
        Assert.Contains("Fonte: Wikipedia", metadata.Description);
        Assert.Equal(timings.Shots.Count, metadata.Media.Count);
        Assert.Contains(metadata.Media, m => m.Source == "Natural Earth");
        Assert.DoesNotContain("#", string.Join("", metadata.Hashtags));
        // The painting is never passed off as a picture of the time.
        Assert.Contains("Capa: ilustração gerada por IA (@cf/black-forest-labs/flux-2-klein-4b), não é uma imagem da época.", metadata.Description);

        // A second run of the same folder reuses everything: no new voice request and no new painting.
        var again = await pipeline.RunAsync(run, CancellationToken.None);
        Assert.Equal(script.SpokenSections.Count, handler.Requests.Count);
        Assert.Single(cloudflare.Requests);
        Assert.Equal(paintedThumbnail, await File.ReadAllBytesAsync(Path.Combine(run, Thumbnail.FileName)));
        Assert.Equal(result.Duration, again.Duration, 3);

        // The owner edits the scene and Cloudflare is out of its daily neurons: one request, then the archive picture,
        // and the description no longer mentions an AI cover.
        await File.WriteAllTextAsync(Path.Combine(run, "script.json"),
            JsonSerializer.Serialize(script with { ThumbnailScene = "A quiet harbor at night" }, Json.Options));
        paintFails = true;
        await pipeline.RunAsync(run, CancellationToken.None);
        Assert.Equal(2, cloudflare.Requests.Count);
        Assert.NotEqual(paintedThumbnail, await File.ReadAllBytesAsync(Path.Combine(run, Thumbnail.FileName)));
        metadata = JsonSerializer.Deserialize<VideoMetadata>(await File.ReadAllTextAsync(Path.Combine(run, "metadata.json")), Json.Options)!;
        Assert.DoesNotContain("gerada por IA", metadata.Description);
    }

    private static MediaItem Item(string id, MediaKind kind, string path, int section) => new()
    {
        Id = id,
        Kind = kind,
        Source = "Test",
        Title = id,
        Author = "FFmpeg test source",
        License = "Public domain",
        SourceUrl = "https://example.org/" + id,
        DownloadUrl = "https://example.org/" + id,
        Width = 1600,
        Height = 1200,
        Section = section,
        LocalPath = path,
    };

    /// <summary>SSIM (0 to 1) between frame <paramref name="frame"/> of the run's video and its thumbnail.jpg.</summary>
    private static async Task<double> SimilarityAsync(string run, int frame)
    {
        var log = Path.Combine(run, $"ssim-{frame}.txt");
        await ProcessRunner.RunCheckedAsync("ffmpeg",
            ["-hide_banner", "-loglevel", "error", "-i", "video.mp4", "-i", Thumbnail.FileName, "-lavfi",
                $"[0:v]trim=start_frame={frame}:end_frame={frame + 1},setpts=PTS-STARTPTS,format=yuv420p[a];[1:v]format=yuv420p[b];[a][b]ssim=stats_file=ssim-{frame}.txt",
                "-f", "null", "-"],
            run, TimeSpan.FromMinutes(1), CancellationToken.None);
        var line = (await File.ReadAllLinesAsync(log)).First();
        return double.Parse(System.Text.RegularExpressions.Regex.Match(line, @"All:([0-9.]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A multipart field with a quoted name whose value starts with <paramref name="value"/>, past its headers.</summary>
    private static string FormField(string name, string value) =>
        $@"name=""{name}""\r\n(?:[^\r]+\r\n)*\r\n{System.Text.RegularExpressions.Regex.Escape(value)}";

    private static MediaGatherer NoGatherer() => new([], [], null!, null!, new MediaOptions(), 1944);
}
