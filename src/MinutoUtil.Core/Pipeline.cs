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
    MediaGatherer gatherer)
{
    private string Tools => Path.Combine(repositoryRoot, "tools");

    public async Task<RunResult> RunAsync(string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var video = config.Video;

        var sources = await SourcesAsync(folder, ct);
        var (script, writerName) = await ScriptAsync(folder, sources, ct);
        Log.Info($"script: \"{script.Title}\", {script.WordCount} words, {script.Sections.Count} sections");

        var timings = await narrator.NarrateAsync(script, folder, ct);
        // Whole frames, so the shots, the captions and the audio all end on the same frame.
        var duration = Math.Ceiling((timings.Duration + video.TailSeconds) * video.Fps) / video.Fps;
        Log.Info($"voice: {timings.Voice}, {timings.Duration:0.00}s spoken, alignment {string.Join(" ", timings.Checks.Select(c => c.Score.ToString("0.00")))}");

        var clipSeconds = config.Media.ClipSeconds;
        var shots = ShotPlanner.Plan(timings, duration, video, clipSeconds);
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
            manifest = await gatherer.GatherAsync(script, stills, clipLength, [config.Story.EnglishArticle], mediaFolder, ct);
            await manifest.SaveAsync(manifestPath, ct);
        }
        else
        {
            Log.Info($"media: reused {manifest.Images.Count} picture(s){(manifest.Clip is null ? "" : " and the clip")} from {manifestPath}");
        }
        if (manifest.Clip is null && shots[0].Effect == ShotEffect.Clip)
            shots = ShotPlanner.Plan(timings, duration, video, 0);
        if (manifest.Images.Count == 0) throw new InvalidOperationException("No usable pictures were found; nothing to render.");

        shots = AssignMedia(shots, manifest);
        var byId = manifest.All.ToDictionary(m => m.Id);
        var renderer = new ShotRenderer(video, Tools);
        var shotFiles = new List<string>();
        foreach (var shot in shots)
            shotFiles.Add(await renderer.RenderAsync(shot, byId[shot.MediaId!], ShotPlanner.SegmentFrames(shots, shot.Index, fade), Path.Combine(folder, "shots"), ct));

        CopyFonts(folder);
        var stampEnd = (double)shots[0].End / video.Fps;
        await File.WriteAllTextAsync(Path.Combine(folder, "captions.ass"),
            CaptionBuilder.Build(timings, duration, video.FontName, video.StampFontName, script.EraStamp, script.PlaceStamp, stampEnd), ct);

        var videoPath = Path.Combine(folder, "video.mp4");
        await final.RenderAsync(shots, shotFiles, Path.Combine(folder, "voice.wav"), duration, folder, videoPath, ct);
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

        var used = shots.Select(s => byId[s.MediaId!]).DistinctBy(m => m.Id).ToList();
        var metadata = Metadata(script, sources, used, duration, timings.Voice, writerName, folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "metadata.json"), JsonSerializer.Serialize(metadata, Json.Options), ct);

        return new RunResult(folder, videoPath, sheet, duration, timings.Voice, probe, lufs);
    }

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
                await File.WriteAllTextAsync(Path.Combine(folder, "script.original.json"), saved, ct);
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
    /// Gives each shot its picture: the clip to the clip shot, then each section's own pictures in rank order, then
    /// spare pictures from other sections, and only when nothing is left a picture already shown.
    /// </summary>
    internal static IReadOnlyList<Shot> AssignMedia(IReadOnlyList<Shot> shots, MediaManifest manifest)
    {
        var used = new HashSet<string>();
        var result = new List<Shot>();
        foreach (var shot in shots)
        {
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
        double duration, string voice, string writer, string folder)
    {
        var description = new StringBuilder(script.Description.Trim());
        description.AppendLine().AppendLine();
        description.AppendLine($"Fonte: Wikipedia ({string.Join("; ", sources.Select(s => $"{s.Title}, {s.Url}"))}). Texto sob CC BY-SA 4.0.");
        description.AppendLine();
        description.AppendLine("Créditos:");
        foreach (var item in media)
            description.AppendLine($"- {item.Title}. {item.Author}. {item.License}. {item.Source}: {item.SourceUrl}");
        description.AppendLine();
        description.Append(string.Join(' ', script.Hashtags.Select(h => "#" + h)));
        return new VideoMetadata(script.Title, description.ToString(), script.Hashtags, duration, voice, writer,
            media.Select(m => new MediaCredit(m.Title, m.Author, m.License, m.Source, m.SourceUrl, m.Kind.ToString().ToLowerInvariant(),
                Path.GetRelativePath(folder, m.LocalPath!).Replace('\\', '/'))).ToList(),
            sources.Select(s => s.Url).ToList());
    }
}
