using System.Globalization;
using System.Text;
using MinutoUtil.Core.Media;

namespace MinutoUtil.Core.Video;

/// <summary>
/// Renders each shot into its own 1080x1920, 30 fps file: stills through tools/motion.py (Ken Burns or parallax), the
/// footage clip with FFmpeg. A shot whose inputs did not change is reused, so a re-render only redoes what changed.
/// </summary>
public sealed class ShotRenderer(VideoOptions options, string toolsDirectory)
{
    /// <summary>Bump when the look of a shot changes, so cached shots are made again.</summary>
    private const string RenderVersion = "6";

    public async Task<string> RenderAsync(Shot shot, MediaItem media, int frames, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"shot-{shot.Index:00}.mp4");
        var stamp = path + ".key";
        var key = $"{RenderVersion}|{shot.Effect}|{shot.Direction}|{shot.ZoomIn}|{frames}|{media.LocalPath}|{new FileInfo(media.LocalPath!).Length}|" +
                  $"{options.Width}x{options.Height}@{options.Fps}|{options.MotionSupersample}|{options.KenBurnsZoom}|{options.ParallaxTravel}";
        if (File.Exists(path) && File.Exists(stamp) && await File.ReadAllTextAsync(stamp, ct) == key)
        {
            Log.Info($"shot {shot.Index}: reused ({shot.Effect})");
            return path;
        }

        var started = DateTime.UtcNow;
        if (shot.Effect == ShotEffect.Clip) await RenderClipAsync(media, frames, path, ct);
        else await RenderStillAsync(shot, media, frames, path, ct);
        await File.WriteAllTextAsync(stamp, key, ct);
        Log.Info($"shot {shot.Index}: {shot.Effect} of {Path.GetFileName(media.LocalPath)}, {frames} frames, {(DateTime.UtcNow - started).TotalSeconds:0}s");
        return path;
    }

    private async Task RenderStillAsync(Shot shot, MediaItem media, int frames, string path, CancellationToken ct)
    {
        var depth = Path.ChangeExtension(media.LocalPath!, ".depth.png");
        List<string> arguments =
        [
            "run", "--quiet", "motion.py", "render",
            "--image", Path.GetFullPath(media.LocalPath!),
            "--out", Path.GetFullPath(path),
            "--effect", shot.Effect == ShotEffect.Parallax ? "parallax" : "kenburns",
            "--frames", frames.ToString(CultureInfo.InvariantCulture),
            "--fps", options.Fps.ToString(CultureInfo.InvariantCulture),
            "--width", options.Width.ToString(CultureInfo.InvariantCulture),
            "--height", options.Height.ToString(CultureInfo.InvariantCulture),
            "--direction", shot.Direction.ToString(CultureInfo.InvariantCulture),
            "--zoom", shot.ZoomIn ? "in" : "out",
            "--supersample", Num(options.MotionSupersample),
            "--travel", Num(options.ParallaxTravel),
            "--kb-zoom", Num(options.KenBurnsZoom),
            "--depth", Path.GetFullPath(depth),
            "--ffmpeg", options.FfmpegPath,
        ];
        await ProcessRunner.RunCheckedAsync("uv", arguments, toolsDirectory, TimeSpan.FromMinutes(20), ct);
    }

    /// <summary>
    /// Footage at 1080p or more fills the frame (cropped to 9:16 with a slow push-in); smaller footage would turn to mush
    /// that way, so it sits full width over a blurred, darkened copy of itself. Never black bars.
    /// </summary>
    private async Task RenderClipAsync(MediaItem media, int frames, string path, CancellationToken ct)
    {
        var (w, h, fps) = (options.Width, options.Height, options.Fps);
        var seconds = Num((double)frames / fps);
        var fill = media.Height >= 900;
        string filter;
        if (fill)
        {
            filter = $"[0:v]fps={fps},setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration={seconds}," +
                     $"scale={w}:{h}:force_original_aspect_ratio=increase:flags=lanczos,crop={w}:{h},setsar=1," +
                     $"trim=end_frame={frames},format=yuv420p[v]";
        }
        else
        {
            var fgWidth = (int)(w * 1.18) / 2 * 2;
            filter = $"[0:v]fps={fps},setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration={seconds},trim=end_frame={frames},setsar=1,split=2[b][f];" +
                     $"[b]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},gblur=sigma=32,eq=brightness=-0.18[bg];" +
                     $"[f]scale={fgWidth}:-2:flags=lanczos,crop={w}:ih[fg];" +
                     $"[bg][fg]overlay=x=0:y=(H-h)/2-80,format=yuv420p[v]";
        }
        await ProcessRunner.RunCheckedAsync(options.FfmpegPath,
            ["-y", "-hide_banner", "-loglevel", "error", "-i", Path.GetFullPath(media.LocalPath!), "-filter_complex", filter, "-map", "[v]",
             "-an", "-frames:v", frames.ToString(CultureInfo.InvariantCulture), "-r", fps.ToString(CultureInfo.InvariantCulture),
             "-c:v", "libx264", "-preset", "medium", "-crf", "12", "-pix_fmt", "yuv420p", Path.GetFullPath(path)],
            Path.GetDirectoryName(Path.GetFullPath(path))!, TimeSpan.FromMinutes(5), ct);
    }

    internal static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>Joins the shots with cross-fades, burns in the captions and mixes the voice at the target loudness.</summary>
public sealed class FinalRenderer(VideoOptions options, Ffmpeg ffmpeg)
{
    public int FadeFrames => Math.Max(2, (int)Math.Round(options.FadeSeconds * options.Fps / 2) * 2);

    /// <param name="workFolder">Holds captions.ass and fonts/: FFmpeg runs there so the filter graph needs no Windows paths.</param>
    public async Task RenderAsync(IReadOnlyList<Shot> shots, IReadOnlyList<string> shotFiles, string voicePath, double duration,
        string workFolder, string outputPath, CancellationToken ct)
    {
        var loudness = await ffmpeg.LoudnessAsync(voicePath, ct) ?? throw new ToolException("The narration is silent.");
        var gain = Math.Round(options.VoiceLufs - loudness, 2);
        var arguments = Arguments(shots, shotFiles, voicePath, duration, gain, outputPath);
        Log.Info($"render: {shots.Count} shots, {duration:0.00}s, voice {loudness:0.0} LUFS, gain {gain:+0.0;-0.0} dB");
        await ProcessRunner.RunCheckedAsync(options.FfmpegPath, arguments, workFolder, TimeSpan.FromMinutes(20), ct);
    }

    internal IReadOnlyList<string> Arguments(IReadOnlyList<Shot> shots, IReadOnlyList<string> shotFiles, string voicePath,
        double duration, double gainDb, string outputPath)
    {
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-nostdin" };
        foreach (var file in shotFiles) args.AddRange(["-i", Path.GetFullPath(file)]);
        args.AddRange(["-i", Path.GetFullPath(voicePath)]);
        args.AddRange(["-filter_complex", FilterGraph(shots, duration, gainDb)]);
        args.AddRange(["-map", "[vout]", "-map", "[aout]", "-r", options.Fps.ToString(CultureInfo.InvariantCulture)]);
        // CRF 18 with a peak cap: grainy archive footage otherwise needs ~6 Mbit/s, and the file must stay under the
        // 50 MB a Telegram bot can send (video.maxVideoKbps 4500 is about 35 MB a minute).
        var kbps = options.MaxVideoKbps.ToString(CultureInfo.InvariantCulture);
        args.AddRange(["-c:v", options.Encoder, "-preset", "medium", "-crf", "18", "-maxrate", $"{kbps}k", "-bufsize", $"{options.MaxVideoKbps * 2}k",
            "-profile:v", "high", "-pix_fmt", "yuv420p"]);
        args.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-t", ShotRenderer.Num(duration), "-movflags", "+faststart", Path.GetFullPath(outputPath)]);
        return args;
    }

    internal string FilterGraph(IReadOnlyList<Shot> shots, double duration, double gainDb)
    {
        var fps = options.Fps;
        var half = FadeFrames / 2;
        var graph = new StringBuilder();
        for (var k = 0; k < shots.Count; k++)
            graph.Append($"[{k}:v]fps={fps},settb=AVTB,setpts=PTS-STARTPTS,format=yuv420p,setsar=1[s{k}];");

        var last = "s0";
        for (var k = 1; k < shots.Count; k++)
        {
            // The fade is centered on the cut: it starts half a fade before it (see ShotPlanner.SegmentFrames).
            var offset = (double)(shots[k].Start - half) / fps;
            graph.Append($"[{last}][s{k}]xfade=transition=fade:duration={ShotRenderer.Num(2.0 * half / fps)}:offset={ShotRenderer.Num(offset)}[x{k}];");
            last = $"x{k}";
        }
        graph.Append($"[{last}]ass=captions.ass:fontsdir=fonts,format=yuv420p[vout];");

        // The measured gain brings the narration to the target loudness exactly; a limiter only catches peaks above
        // -1.5 dBFS. (A single-pass loudnorm landed about 0.8 LU low and reshaped the dynamics.)
        graph.Append($"[{shots.Count}:a]aresample=48000,aformat=channel_layouts=mono,highpass=f=70,volume={ShotRenderer.Num(gainDb)}dB,")
            .Append("alimiter=limit=0.84:attack=5:release=60:level=disabled,")
            .Append($"apad,atrim=0:{ShotRenderer.Num(duration)},aformat=channel_layouts=stereo[aout]");
        return graph.ToString();
    }
}

/// <summary>A grid of frames, one every few seconds with its time, to review a video at a glance.</summary>
public static class ContactSheet
{
    public static async Task MakeAsync(VideoOptions options, string videoPath, double duration, string fontsFolderName, string fontFileName,
        string outputPath, CancellationToken ct, double every = 3, int columns = 6)
    {
        var count = (int)Math.Floor(duration / every) + 1;
        columns = Math.Min(columns, count);
        var rows = (int)Math.Ceiling(count / (double)columns);
        var folder = Path.GetDirectoryName(Path.GetFullPath(videoPath))!;
        // Every n-th frame exactly (the fps filter can drop the last one at the end of the file).
        var step = Math.Max(1, (int)Math.Round(every * options.Fps));
        var filter = $"select='not(mod(n\\,{step}))',scale=270:480," +
                     $"drawtext=fontfile={fontsFolderName}/{fontFileName}:text='%{{pts\\:hms}}':x=8:y=8:fontsize=24:fontcolor=white:box=1:boxcolor=black@0.6:boxborderw=4," +
                     $"tile={columns}x{rows}:padding=4:margin=4:color=0x202020";
        await ProcessRunner.RunCheckedAsync(options.FfmpegPath,
            ["-y", "-hide_banner", "-loglevel", "error", "-i", Path.GetFullPath(videoPath), "-vf", filter, "-frames:v", "1", "-update", "1", Path.GetFullPath(outputPath)],
            folder, TimeSpan.FromMinutes(3), ct);
    }
}
