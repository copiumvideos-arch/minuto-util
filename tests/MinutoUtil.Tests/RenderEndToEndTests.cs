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
            await File.ReadAllTextAsync(TestPaths.Fixture("script-answer.json")), Json.Options)!);
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
            Generated.ToneAsync(tone, Math.Clamp(text.Length / 45.0, 1.2, 3.2), 300 + text.Length).GetAwaiter().GetResult();
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

        var config = new AppConfig { Video = new VideoOptions { MotionSupersample = 1.0 }, Voice = voiceOptions };
        var pipeline = new Pipeline(config, root, new WikipediaClient(new HttpClient(new FakeHandler((_, _) => throw new InvalidOperationException("no network")))),
            () => throw new InvalidOperationException("the cached script must be used"), narrator, NoGatherer());

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

        foreach (var file in new[] { "video.mp4", "script.json", "metadata.json", "timings.json", "contact-sheet.png", "captions.ass" })
            Assert.True(File.Exists(Path.Combine(run, file)), file);

        var timings = JsonSerializer.Deserialize<TimingsFile>(await File.ReadAllTextAsync(Path.Combine(run, "timings.json")), Json.Options)!;
        Assert.Equal("clip", timings.Shots[0].Effect);
        Assert.Contains(timings.Shots, s => s.Effect == "parallax");
        Assert.Contains(timings.Shots, s => s.Effect == "kenburns");
        Assert.Equal(timings.Shots.Count, timings.Shots.Select(s => s.Media).Distinct().Count());
        Assert.Equal(result.Duration, timings.Shots[^1].End, 2);

        var metadata = JsonSerializer.Deserialize<VideoMetadata>(await File.ReadAllTextAsync(Path.Combine(run, "metadata.json")), Json.Options)!;
        Assert.Contains("Créditos:", metadata.Description);
        Assert.Contains("Fonte: Wikipedia", metadata.Description);
        Assert.Equal(timings.Shots.Count, metadata.Media.Count);
        Assert.DoesNotContain("#", string.Join("", metadata.Hashtags));

        // A second run of the same folder reuses everything: no new voice request.
        var again = await pipeline.RunAsync(run, CancellationToken.None);
        Assert.Equal(script.SpokenSections.Count, handler.Requests.Count);
        Assert.Equal(result.Duration, again.Duration, 3);
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

    private static MediaGatherer NoGatherer() => new([], [], null!, null!, new MediaOptions(), 1944);
}
