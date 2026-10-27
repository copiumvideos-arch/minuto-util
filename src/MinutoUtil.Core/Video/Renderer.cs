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

    /// <summary>
    /// Renders a shot, or reuses an earlier render of exactly the same inputs. Files are named by a hash of their
    /// inputs, so a shot that only moved in the order (a map shot inserted before it) is still reused.
    /// </summary>
    public async Task<string> RenderAsync(Shot shot, MediaItem media, int frames, string folder, CancellationToken ct)
    {
        var key = $"{RenderVersion}|{shot.Effect}|{shot.Direction}|{shot.ZoomIn}|{frames}|{Path.GetFileName(media.LocalPath)}|{new FileInfo(media.LocalPath!).Length}|" +
                  $"{options.Width}x{options.Height}@{options.Fps}|{options.MotionSupersample}|{options.KenBurnsZoom}|{options.ParallaxTravel}";
        return await CachedAsync(shot, key, folder, $"{shot.Effect} of {Path.GetFileName(media.LocalPath)}, {frames} frames", path =>
            shot.Effect == ShotEffect.Clip ? RenderClipAsync(media, frames, path, ct) : RenderStillAsync(shot, media, frames, path, ct), ct);
    }

    /// <summary>The map shot: tools/mapinsert.py zooms from Europe into the place and pins it.</summary>
    public async Task<string> RenderMapAsync(Shot shot, Scripts.ScriptLocation location, int frames, string fontPath, string dataDirectory,
        string folder, CancellationToken ct)
    {
        var key = $"{RenderVersion}|map2|{location.Name}|{location.Latitude}|{location.Longitude}|{frames}|{options.Width}x{options.Height}@{options.Fps}";
        return await CachedAsync(shot, key, folder, $"map of {location.Name}, {frames} frames", path =>
            ProcessRunner.RunCheckedAsync("uv",
            [
                "run", "--quiet", "mapinsert.py", "--out", Path.GetFullPath(path), "--name", location.Name,
                "--lat", Num(location.Latitude), "--lon", Num(location.Longitude), "--frames", frames.ToString(CultureInfo.InvariantCulture),
                "--font", Path.GetFullPath(fontPath), "--data-dir", Path.GetFullPath(dataDirectory),
                "--fps", options.Fps.ToString(CultureInfo.InvariantCulture), "--width", options.Width.ToString(CultureInfo.InvariantCulture),
                "--height", options.Height.ToString(CultureInfo.InvariantCulture), "--ffmpeg", options.FfmpegPath,
            ], toolsDirectory, TimeSpan.FromMinutes(10), ct), ct);
    }

    private static async Task<string> CachedAsync(Shot shot, string key, string folder, string what, Func<string, Task> render, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
        var path = Path.Combine(folder, $"shot-{hash}.mp4");
        if (File.Exists(path))
        {
            Log.Info($"shot {shot.Index}: reused ({what})");
            return path;
        }
        var started = DateTime.UtcNow;
        var temp = Path.Combine(folder, $"shot-{hash}.partial.mp4");
        await render(temp);
        File.Move(temp, path, overwrite: true);
        Log.Info($"shot {shot.Index}: {what}, {(DateTime.UtcNow - started).TotalSeconds:0}s");
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
    private StyleOptions Style => options.Style;

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

    /// <summary>
    /// The picture's look, under the captions: light denoise, sharpening (before the grain, so the grain is not
    /// sharpened), contrast and saturation with a faint brightness flicker, a slight warm push in the mid tones, faded
    /// blacks and whites, a vignette, then grain. Every step is a switch in <see cref="StyleOptions"/>.
    /// </summary>
    internal string PictureChain(double duration)
    {
        var steps = new List<string>();
        if (Style.Denoise) steps.Add("hqdn3d=1.5:1.2:3:3");
        if (Style.Sharpen > 0) steps.Add($"unsharp=5:5:{ShotRenderer.Num(Style.Sharpen)}:5:5:0");
        var flicker = Style.Vintage ? ":brightness='0.009*sin(2*PI*t*5.3)+0.006*sin(2*PI*t*13.1)':eval=frame" : "";
        if (Style.Contrast != 1 || Style.Saturation != 1 || flicker.Length > 0)
            steps.Add($"eq=contrast={ShotRenderer.Num(Style.Contrast)}:saturation={ShotRenderer.Num(Style.Saturation)}{flicker}");
        if (Style.Vintage)
        {
            var w = ShotRenderer.Num(Style.Warmth);
            // Mid tones only: gray footage gets the faintest warmth, not a sepia tint.
            steps.Add($"colorbalance=rm={w}:gm={ShotRenderer.Num(Style.Warmth / 3)}:bm=-{w}");
            steps.Add("curves=all='0/0.035 0.5/0.5 1/0.965'");
            if (Style.Vignette > 0) steps.Add($"vignette=angle={ShotRenderer.Num(Style.Vignette)}");
            steps.Add("format=yuv420p");
            if (Style.Grain > 0) steps.Add($"noise=c0s={Style.Grain}:c0f=t");
        }
        steps.Add("format=yuv420p");
        return string.Join(',', steps);
    }

    /// <summary>The two cuts that get a light leak: about a third and two thirds in, never the clip or the map.</summary>
    internal IReadOnlyList<double> LeakTimes(IReadOnlyList<Shot> shots)
    {
        if (!Style.Vintage || !Style.LightLeaks || shots.Count < 4) return [];
        var half = FadeFrames / 2;
        return new[] { shots.Count / 3, shots.Count * 2 / 3 }
            .Distinct()
            .Where(i => i > 0 && shots[i].Effect is not (ShotEffect.Map or ShotEffect.Clip) && shots[i - 1].Effect is not ShotEffect.Map)
            .Select(i => Math.Max(0, (double)(shots[i].Start - half) / options.Fps - 0.3))
            .ToList();
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
        graph.Append($"[{last}]{PictureChain(duration)}");
        foreach (var (start, k) in LeakTimes(shots).Select((t, k) => (t, k)))
        {
            // A warm glow from one side over a cross-fade, rising and falling over 0.9 s. Generated only for those
            // frames, then shifted to the cut.
            var side = k % 2 == 0 ? "0.85" : "0.15";
            graph.Append($"[pic{k}];color=c=black:s={options.Width}x{options.Height}:r={fps}:d=0.9,format=rgba,")
                .Append($"geq=r=255:g=138:b=48:a='255*0.42*sin(PI*T/0.9)*exp(-(pow((X-{side}*W)/(0.55*W),2)+pow((Y-0.3*H)/(0.55*H),2)))',")
                .Append($"setpts=PTS+{ShotRenderer.Num(start)}/TB[leak{k}];[pic{k}][leak{k}]overlay=eof_action=pass:format=auto");
        }
        graph.Append(",ass=captions.ass:fontsdir=fonts,format=yuv420p[vout];");

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
