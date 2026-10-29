using System.Text;
using System.Text.Json;
using MinutoUtil.Core.Llm;
using MinutoUtil.Core.Media;
using MinutoUtil.Core.Scripts;
using MinutoUtil.Core.Sources;
using MinutoUtil.Core.Video;
using MinutoUtil.Core.Voice;

namespace MinutoUtil.Core;

/// <summary>What a finished run produced.</summary>
public sealed record RunResult(string Folder, string VideoPath, string ContactSheetPath, double Duration, string Voice, ProbeInfo Probe, double? Lufs);

/// <summary>The upload copy written next to the video. Nothing is uploaded by this app.</summary>
public sealed record VideoMetadata(string Title, string Description, IReadOnlyList<string> Hashtags, double DurationSeconds,
    string Voice, string Writer, IReadOnlyList<MediaCredit> Media, IReadOnlyList<string> Sources);

public sealed record MediaCredit(string Title, string Author, string License, string Source, string SourceUrl, string Kind, string File);

/// <summary>Everything written to timings.json: the words, the sections and the shots, in seconds.</summary>
public sealed record TimingsFile(double Duration, double VoiceDuration, IReadOnlyList<SectionTime> Sections, IReadOnlyList<SpokenWord> Words,
    IReadOnlyList<AlignCheck> Checks, IReadOnlyList<ShotTiming> Shots);

public sealed record ShotTiming(int Index, int Section, double Start, double End, string Effect, string Media);

/// <summary>
/// One video from start to finish, in a run folder. Every step saves its result there and a re-run of the same folder
/// reuses it (sources, script, voice takes, media, rendered shots), so fixing the look never calls the APIs again.
/// </summary>
public sealed class Pipeline(
    AppConfig config,
    string repositoryRoot,
    WikipediaClient wikipedia,
    Func<ScriptWriter> writer,
    Narrator narrator,
    MediaGatherer gatherer,
    ICoverPainter? painter = null,
    SteamTrailers? trailers = null)
{
    private string Tools => Path.Combine(repositoryRoot, "tools");

    /// <param name="scriptOnly">Stop after the script (review mode): sources.json and script.json are saved and a later
    /// run of the same folder goes on from there. Returns null then.</param>
    public async Task<RunResult?> RunAsync(string folder, CancellationToken ct, bool scriptOnly = false)
    {
        Directory.CreateDirectory(folder);
        var video = config.Video;

        var sources = await SourcesAsync(folder, ct);
        var (script, writerName) = await ScriptAsync(folder, sources, ct);
        Log.Info($"script: \"{script.Title}\", {script.WordCount} words, {script.Sections.Count} sections");
        if (scriptOnly) return null;

        var timings = await narrator.NarrateAsync(script, folder, ct);
        // Whole frames, so the shots, the captions and the audio all end on the same frame.
        var duration = Math.Ceiling((timings.Duration + video.TailSeconds) * video.Fps) / video.Fps;
        Log.Info($"voice: {timings.Voice}, {timings.Duration:0.00}s spoken, alignment {string.Join(" ", timings.Checks.Select(c => c.Score.ToString("0.00")))}");
        if (config.Media.Footage == "trailers") return await TrailerVideoAsync(folder, script, sources, timings, writerName, duration, ct);

        var clipSeconds = config.Media.ClipSeconds;
        // The map shot needs the place on the map; scripts written before the field existed simply go without.
        var mapSeconds = video.Style.MapInsert && script.Location is not null ? video.Style.MapSeconds : 0;
        if (video.Style.MapInsert && script.Location is null) Log.Warn("style: no location in the script, so no map shot");
        var shots = ShotPlanner.Plan(timings, duration, video, clipSeconds, mapSeconds);
        var final = new FinalRenderer(video, new Ffmpeg(video));
        var fade = final.FadeFrames;

        var mediaFolder = Path.Combine(folder, "media");
        var manifestPath = Path.Combine(mediaFolder, "manifest.json");
        var manifest = MediaManifest.Load(manifestPath);
        if (manifest is null)
        {
            // Every section needs its stills; the hook needs one too in case no clip is found.
            var stills = Enumerable.Range(0, timings.Sections.Count)
                .Select(s => Math.Max(shots.Count(x => x.Section == s && x.Effect != ShotEffect.Clip), s == 0 ? 1 : 0)).ToList();
            var clipLength = shots[0].Effect == ShotEffect.Clip ? (ShotPlanner.SegmentFrames(shots, 0, fade) + 6.0) / video.Fps : 0;
            manifest = await gatherer.GatherAsync(script, stills, clipLength, [config.Story.EnglishArticle], mediaFolder, ct,
                sources.Select(s => (s.Language, s.Title)).ToList());
            await manifest.SaveAsync(manifestPath, ct);
        }
        else
        {
            Log.Info($"media: reused {manifest.Images.Count} picture(s){(manifest.Clip is null ? "" : " and the clip")} from {manifestPath}");
        }
        if (manifest.Clip is null && shots[0].Effect == ShotEffect.Clip)
            shots = ShotPlanner.Plan(timings, duration, video, 0, mapSeconds);
        if (manifest.Images.Count == 0) throw new InvalidOperationException("No usable pictures were found; nothing to render.");

        shots = AssignMedia(shots, manifest);
        var byId = manifest.All.ToDictionary(m => m.Id);
        var renderer = new ShotRenderer(video, Tools);
        var shotFiles = new List<string>();
        foreach (var shot in shots)
        {
            var frames = ShotPlanner.SegmentFrames(shots, shot.Index, fade);
            shotFiles.Add(shot.Effect == ShotEffect.Map
                ? await renderer.RenderMapAsync(shot, script.Location!, frames, Path.Combine(repositoryRoot, video.FontFile),
                    Path.Combine(repositoryRoot, video.Style.MapDataDirectory), Path.Combine(folder, "shots"), ct)
                : await renderer.RenderAsync(shot, byId[shot.MediaId!], frames, Path.Combine(folder, "shots"), ct));
        }

        CopyFonts(folder);
        var emblem = EmblemPath();
        var stampEnd = (double)shots[0].End / video.Fps;
        await File.WriteAllTextAsync(Path.Combine(folder, "captions.ass"),
            CaptionBuilder.Build(timings, duration, video.FontName, video.StampFontName, script.EraStamp, script.PlaceStamp, stampEnd, video.Style,
                emblem is not null), ct);

        // Before the video: its first frame is the thumbnail (style.coverFrames).
        var thumbnailPath = Path.Combine(folder, Thumbnail.FileName);
        PaintedCover? coverArt = null;
        if (video.Style.Thumbnail)
        {
            // The first still the viewer sees (the hook's picture when the hook is not footage).
            var cover = shots.FirstOrDefault(s => s.Effect is ShotEffect.KenBurns or ShotEffect.Parallax) is { } still
                ? byId[still.MediaId!]
                : manifest.Images[0];
            var text = string.IsNullOrWhiteSpace(script.ThumbnailText) ? script.Title : script.ThumbnailText;
            coverArt = await CoverArtAsync(folder, script, ct);
            await Thumbnail.RenderAsync(video, coverArt?.Path ?? cover.LocalPath!, text, script.EraStamp, folder, emblem, thumbnailPath, ct);
        }
        else if (File.Exists(thumbnailPath))
        {
            // Switched off after an earlier render: an old file must not be uploaded.
            File.Delete(thumbnailPath);
        }

        var videoPath = Path.Combine(folder, "video.mp4");
        await final.RenderAsync(shots, shotFiles, Path.Combine(folder, "voice.wav"), duration, folder, videoPath, ct, emblem,
            video.Style.Thumbnail ? thumbnailPath : null);
        var ffmpeg = new Ffmpeg(video);
        var probe = await ffmpeg.ProbeAsync(videoPath, ct);
        var lufs = await ffmpeg.LoudnessAsync(videoPath, ct);
        Log.Info($"video: {probe}, {lufs:0.0} LUFS");
        if (probe.Width != video.Width || probe.Height != video.Height || !probe.HasAudio || Math.Abs(probe.Duration - duration) > 0.3)
            throw new ToolException($"The rendered video looks wrong: {probe}");

        var sheet = Path.Combine(folder, "contact-sheet.png");
        await ContactSheet.MakeAsync(video, videoPath, duration, "fonts", Path.GetFileName(video.StampFontFile), sheet, ct);

        var timingsFile = new TimingsFile(duration, timings.Duration, timings.Sections, timings.Words, timings.Checks,
            shots.Select(s => new ShotTiming(s.Index, s.Section, (double)s.Start / video.Fps, (double)s.End / video.Fps,
                s.Effect.ToString().ToLowerInvariant(), s.MediaId!)).ToList());
        await File.WriteAllTextAsync(Path.Combine(folder, "timings.json"), JsonSerializer.Serialize(timingsFile, Json.Options), ct);

        var used = shots.Where(s => s.Effect != ShotEffect.Map).Select(s => byId[s.MediaId!]).DistinctBy(m => m.Id).ToList();
        if (shots.Any(s => s.Effect == ShotEffect.Map)) used.Add(NaturalEarth(Path.Combine(repositoryRoot, video.Style.MapDataDirectory), config.Channel.Name));
        var metadata = Metadata(script, sources, used, duration, timings.Voice, writerName, folder, coverArt?.Model);
        await File.WriteAllTextAsync(Path.Combine(folder, "metadata.json"), JsonSerializer.Serialize(metadata, Json.Options), ct);

        return new RunResult(folder, videoPath, sheet, duration, timings.Voice, probe, lufs);
    }

    /// <summary>
    /// The rest of a run whose footage is the game's trailers (media.footage "trailers"): the trailers from Steam,
    /// tools/trailer.py cuts them on the narration into one silent picture track, then the same final render as the
    /// archive look (captions, voice, loudness), with the channel's own style switches. No map, year card or cover.
    /// </summary>
    private async Task<RunResult> TrailerVideoAsync(string folder, VideoScript script, IReadOnlyList<SourceArticle> sources,
        SpeechTimings timings, string writerName, double duration, CancellationToken ct)
    {
        if (trailers is null) throw new InvalidOperationException("media.footage is \"trailers\" but no Steam trailer source was given.");
        var video = config.Video;
        var found = await trailers.GatherAsync(config.Story.EnglishArticle, config.Media.SteamAppId, Path.Combine(folder, "media"), ct);

        var frames = (int)Math.Round(duration * video.Fps);
        var footage = Path.Combine(folder, "footage.mp4");
        var cutsPath = Path.Combine(folder, "cuts.json");
        var speech = Path.Combine(folder, "voice", "speech-words.json");
        var words = JsonSerializer.Serialize(new { words = timings.Words.Select(w => new { text = w.Text, start = w.Start, end = w.End }) }, Json.Options);
        // Reused while the trailers, the words and the length are the same.
        var key = $"{TrailerVersion}|{frames}|{video.Width}x{video.Height}@{video.Fps}|{words}|" +
                  string.Join('|', found.Select(t => $"{Path.GetFileName(t.LocalPath)}:{new FileInfo(t.LocalPath!).Length}"));
        var keyPath = Path.Combine(folder, "footage.key");
        if (File.Exists(footage) && File.Exists(cutsPath) && File.Exists(keyPath) && await File.ReadAllTextAsync(keyPath, ct) == key)
        {
            Log.Info("footage: reused footage.mp4");
        }
        else
        {
            await File.WriteAllTextAsync(speech, words, ct);
            await ProcessRunner.RunCheckedAsync("uv",
            [
                "run", "--quiet", "trailer.py", "--trailers", Path.GetFullPath(Path.Combine(folder, "media", "trailers")),
                "--timings", Path.GetFullPath(speech), "--frames", Invariant(frames),
                "--out", Path.GetFullPath(footage), "--cuts", Path.GetFullPath(cutsPath),
                "--fps", Invariant(video.Fps), "--width", Invariant(video.Width), "--height", Invariant(video.Height),
                "--ffmpeg", video.FfmpegPath,
            ], Tools, TimeSpan.FromMinutes(30), ct);
            await File.WriteAllTextAsync(keyPath, key, ct);
        }
        var cuts = JsonSerializer.Deserialize<List<TrailerCut>>(await File.ReadAllTextAsync(cutsPath, ct), Json.Options) ?? [];
        Log.Info($"footage: {cuts.Count} cuts from {found.Count} trailer(s)");

        CopyFonts(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "captions.ass"),
            CaptionBuilder.Build(timings, duration, video.FontName, video.StampFontName, "", "", 0, video.Style, emblem: false, icon: false), ct);
        var shots = new List<Shot> { new(0, 0, 0, frames, ShotEffect.Clip, 0, false) { MediaId = "footage" } };
        var videoPath = Path.Combine(folder, "video.mp4");
        var final = new FinalRenderer(video, new Ffmpeg(video));
        await final.RenderAsync(shots, [footage], Path.Combine(folder, "voice.wav"), duration, folder, videoPath, ct);
        // An old cover from an archive render of the same folder must not be uploaded.
        if (File.Exists(Path.Combine(folder, Thumbnail.FileName))) File.Delete(Path.Combine(folder, Thumbnail.FileName));

        var ffmpeg = new Ffmpeg(video);
        var probe = await ffmpeg.ProbeAsync(videoPath, ct);
        var lufs = await ffmpeg.LoudnessAsync(videoPath, ct);
        Log.Info($"video: {probe}, {lufs:0.0} LUFS");
        if (probe.Width != video.Width || probe.Height != video.Height || !probe.HasAudio || Math.Abs(probe.Duration - duration) > 0.3)
            throw new ToolException($"The rendered video looks wrong: {probe}");
        var sheet = Path.Combine(folder, "contact-sheet.png");
        await ContactSheet.MakeAsync(video, videoPath, duration, "fonts", Path.GetFileName(video.StampFontFile), sheet, ct);

        var byFile = found.ToDictionary(t => Path.GetFileName(t.LocalPath!));
        var timingsFile = new TimingsFile(duration, timings.Duration, timings.Sections, timings.Words, timings.Checks,
            cuts.Select((c, i) => new ShotTiming(i, SectionAt(timings, c.Slot[0]), c.Slot[0], c.Slot[1], "trailer",
                byFile.TryGetValue(c.File, out var t) ? t.Id : c.File)).ToList());
        await File.WriteAllTextAsync(Path.Combine(folder, "timings.json"), JsonSerializer.Serialize(timingsFile, Json.Options), ct);

        var used = found.Where(t => cuts.Any(c => c.File == Path.GetFileName(t.LocalPath))).ToList();
        var metadata = Metadata(script, sources, used, duration, timings.Voice, writerName, folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "metadata.json"), JsonSerializer.Serialize(metadata, Json.Options), ct);
        return new RunResult(folder, videoPath, sheet, duration, timings.Voice, probe, lufs);
    }

    /// <summary>Bump when trailer.py cuts differently, so footage.mp4 is made again.</summary>
    private const string TrailerVersion = "1";

    /// <summary>One cut of trailer.py's plan (cuts.json): the trailer file and the slot it fills, in seconds.</summary>
    private sealed record TrailerCut(string File, IReadOnlyList<double> Slot);

    private static int SectionAt(SpeechTimings timings, double time) =>
        Math.Max(0, timings.Sections.ToList().FindLastIndex(s => s.Start <= time + 0.01));

    private static string Invariant(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<IReadOnlyList<SourceArticle>> SourcesAsync(string folder, CancellationToken ct)
    {
        var path = Path.Combine(folder, "sources.json");
        if (File.Exists(path))
        {
            Log.Info("sources: reused sources.json");
            return JsonSerializer.Deserialize<List<SourceArticle>>(await File.ReadAllTextAsync(path, ct), Json.Options)!;
        }
        var story = config.Story;
        var (english, other) = await wikipedia.FetchPairAsync(story.EnglishArticle, config.Channel.Language, story.PortugueseArticle, ct);
        // Portuguese first: the narration is in Portuguese, so its wording is the closer model.
        var sources = other is null ? new List<SourceArticle> { english } : [other, english];
        foreach (var s in sources) Log.Info($"sources: {s.Language} \"{s.Title}\", {s.Text.Length} characters (revision {s.RevisionId})");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(sources, Json.Options), ct);
        return sources;
    }

    private async Task<(VideoScript Script, string Writer)> ScriptAsync(string folder, IReadOnlyList<SourceArticle> sources, CancellationToken ct)
    {
        var path = Path.Combine(folder, "script.json");
        var writerPath = Path.Combine(folder, "script-writer.json");
        if (File.Exists(path))
        {
            Log.Info("script: reused script.json (no model request)");
            var saved = await File.ReadAllTextAsync(path, ct);
            var cached = ScriptWriter.Polish(JsonSerializer.Deserialize<VideoScript>(saved, Json.Options)!, sources);
            var polished = JsonSerializer.Serialize(cached, Json.Options);
            if (polished != saved)
            {
                // Fixes added after the script was written apply to it too; the original is kept beside it.
                var original = Path.Combine(folder, "script.original.json");
                if (!File.Exists(original)) await File.WriteAllTextAsync(original, saved, ct);
                await File.WriteAllTextAsync(path, polished, ct);
            }
            var name = File.Exists(writerPath) ? JsonDocument.Parse(await File.ReadAllTextAsync(writerPath, ct)).RootElement.GetProperty("writer").GetString() ?? "" : "";
            return (cached, name);
        }
        var written = await writer().WriteAsync(config.Story.Topic, sources, config.Story.MaxSourceChars, ct);
        foreach (var warning in written.Warnings) Log.Warn($"script: {warning}");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(written.Script, Json.Options), ct);
        await File.WriteAllTextAsync(writerPath, JsonSerializer.Serialize(new { writer = written.Writer, warnings = written.Warnings }, Json.Options), ct);
        return (written.Script, written.Writer);
    }

    /// <summary>
    /// The AI painting behind the thumbnail text when style.thumbnailPicture is "ai": one request per video, saved as
    /// cover-art.* with cover-art.json and reused while the prompt is the same (an edited scene paints again). Null,
    /// with a warning, when there is no scene or no Cloudflare key, or the request fails: the thumbnail then uses the
    /// archive picture.
    /// </summary>
    private async Task<PaintedCover?> CoverArtAsync(string folder, VideoScript script, CancellationToken ct)
    {
        if (!string.Equals(config.Video.Style.ThumbnailPicture, "ai", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(script.ThumbnailScene))
        {
            Log.Warn("thumbnail: the script has no thumbnailScene, so the cover uses the archive picture");
            return null;
        }
        if (painter is null)
        {
            Log.Warn("thumbnail: no Cloudflare key for the painted cover, so it uses the archive picture");
            return null;
        }

        return await CoverArtFile.PaintOrReuseAsync(folder, script.ThumbnailScene, config.CloudflareAi, painter, ct);
    }

    /// <summary>
    /// Gives each shot its picture: the clip to the clip shot, then each section's own pictures in rank order, then
    /// spare pictures from other sections, and only when nothing is left a picture already shown.
    /// </summary>
    internal static IReadOnlyList<Shot> AssignMedia(IReadOnlyList<Shot> shots, MediaManifest manifest)
    {
        var used = new HashSet<string>();
        var result = new List<Shot>();
        foreach (var shot in shots)
        {
            if (shot.Effect == ShotEffect.Map)
            {
                result.Add(shot);
                continue;
            }
            if (shot.Effect == ShotEffect.Clip && manifest.Clip is not null)
            {
                result.Add(shot with { MediaId = manifest.Clip.Id });
                continue;
            }
            var pick = manifest.Images.FirstOrDefault(i => i.Section == shot.Section && !used.Contains(i.Id))
                       ?? manifest.Images.Where(i => !used.Contains(i.Id)).OrderBy(i => Math.Abs(i.Section - shot.Section)).FirstOrDefault();
            if (pick is null)
            {
                pick = manifest.Images.OrderBy(i => Math.Abs(i.Section - shot.Section)).ThenBy(i => result.Count(r => r.MediaId == i.Id)).First();
                Log.Warn($"shot {shot.Index}: not enough pictures, showing {pick.Id} again");
            }
            used.Add(pick.Id);
            result.Add(shot with { MediaId = pick.Id });
        }
        return result;
    }

    /// <summary>The map's data source, for the credits.</summary>
    private static MediaItem NaturalEarth(string dataDirectory, string channelName) => new()
    {
        Id = "naturalearth",
        Kind = MediaKind.Image,
        Source = "Natural Earth",
        Title = $"Admin 0 countries, 1:10m (map drawn by {channelName})",
        Author = "Natural Earth",
        License = "Public domain",
        SourceUrl = "https://www.naturalearthdata.com/",
        DownloadUrl = "https://www.naturalearthdata.com/",
        LocalPath = Path.Combine(dataDirectory, "ne_10m_admin_0_countries.geojson"),
    };

    /// <summary>The emblem for the watermark, or null (the vector hourglass is drawn) when the watermark is off or the file is missing.</summary>
    private string? EmblemPath()
    {
        var style = config.Video.Style;
        if (!style.Watermark || string.IsNullOrWhiteSpace(style.WatermarkImage)) return null;
        var path = Path.Combine(repositoryRoot, style.WatermarkImage);
        if (File.Exists(path)) return path;
        Log.Warn($"style: no watermark image at {style.WatermarkImage}, so the vector hourglass is drawn");
        return null;
    }

    private void CopyFonts(string folder)
    {
        var fonts = Path.Combine(folder, "fonts");
        Directory.CreateDirectory(fonts);
        foreach (var file in new[] { config.Video.FontFile, config.Video.StampFontFile }.Distinct())
        {
            var source = Path.Combine(repositoryRoot, file);
            File.Copy(source, Path.Combine(fonts, Path.GetFileName(source)), overwrite: true);
        }
    }

    internal static VideoMetadata Metadata(VideoScript script, IReadOnlyList<SourceArticle> sources, IReadOnlyList<MediaItem> media,
        double duration, string voice, string writer, string folder, string? coverArtModel = null)
    {
        // The question closes the first paragraph, which is also the TikTok caption's text: the comments start there.
        var description = new StringBuilder(script.Description.Trim());
        if (!string.IsNullOrWhiteSpace(script.Question)) description.Append($" {script.Question.Trim()}");
        description.AppendLine().AppendLine();
        description.AppendLine($"Fonte: Wikipedia ({string.Join("; ", sources.Select(s => $"{s.Title}, {s.Url}"))}). Texto sob CC BY-SA 4.0.");
        description.AppendLine();
        description.AppendLine("Créditos:");
        foreach (var item in media)
            description.AppendLine($"- {item.Title}. {item.Author}. {item.License}. {item.Source}: {item.SourceUrl}");
        // Said plainly, so nobody takes the painting for a picture of the time.
        if (coverArtModel is not null)
            description.AppendLine($"- Capa: ilustração gerada por IA ({coverArtModel}), não é uma imagem da época.");
        description.AppendLine();
        description.Append(string.Join(' ', script.Hashtags.Select(h => "#" + h)));
        return new VideoMetadata(script.Title, description.ToString(), script.Hashtags, duration, voice, writer,
            media.Select(m => new MediaCredit(m.Title, m.Author, m.License, m.Source, m.SourceUrl, m.Kind.ToString().ToLowerInvariant(),
                Path.GetRelativePath(folder, m.LocalPath!).Replace('\\', '/'))).ToList(),
            sources.Select(s => s.Url).ToList());
    }
}
