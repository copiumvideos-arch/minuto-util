using System.Globalization;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Core.Media;

/// <summary>
/// Downloads are untrusted data: they go into the run's own media folder, are never opened by anything but FFmpeg's
/// decoders and the motion tool's image reader, and are kept only when the bytes really are a picture or video of a
/// usable size (magic bytes, then ffprobe). Nothing downloaded is ever executed.
/// </summary>
public sealed class MediaDownloader(HttpClient http, Ffmpeg ffmpeg, MediaOptions options)
{
    /// <summary>Hosts FFmpeg may read footage from. FFmpeg gets only https URLs on these, never a local or other protocol.</summary>
    internal static readonly string[] FootageHosts = ["archive.org", "upload.wikimedia.org"];

    /// <summary>
    /// Pause between two downloads: Wikimedia's thumbnail servers answer 429 to clients that fetch pictures back to back
    /// (seen 2026-10-27 at one download a second).
    /// </summary>
    internal TimeSpan DownloadInterval { get; init; } = TimeSpan.FromSeconds(2.5);

    private DateTime _lastDownload = DateTime.MinValue;

    public async Task<MediaItem?> DownloadImageAsync(MediaItem item, string folder, string name, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        byte[] data;
        try
        {
            var pause = _lastDownload + DownloadInterval - DateTime.UtcNow;
            if (pause > TimeSpan.Zero) await Task.Delay(pause, ct);
            _lastDownload = DateTime.UtcNow;
            var response = await http.GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            // Wikimedia's media servers count a one-minute window and say when it resets (x-ratelimit-reset, seconds);
            // "Retry-After: 1" alone was not enough on 2026-10-27. One wait of at least 10 s, at most 65 s, never a loop.
            var wait = RateLimitWait(response);
            if ((int)response.StatusCode == 429 && wait <= TimeSpan.FromSeconds(65))
            {
                // One wait for a rate limit (Wikimedia's thumbnail servers), never a loop.
                response.Dispose();
                Log.Warn($"{item.Id}: download rate limited, waiting {wait.TotalSeconds:0}s once");
                await Task.Delay(wait, ct);
                response = await http.GetAsync(item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            using var _ = response;
            if (!response.IsSuccessStatusCode)
            {
                var detail = (int)response.StatusCode == 429
                    ? $" (Retry-After {response.Headers.RetryAfter?.Delta?.TotalSeconds}, reset {string.Join(",", response.Headers.TryGetValues("x-ratelimit-reset", out var r) ? r : [])}: " +
                      $"{TextUtil.Truncate(await response.Content.ReadAsStringAsync(ct), 160).ReplaceLineEndings(" ")})"
                    : "";
                Log.Warn($"skipped {item.Id}: download returned {(int)response.StatusCode}{detail}");
                return null;
            }
            data = await SafeHttp.ReadBytesLimitedAsync(response, options.MaxDownloadBytes, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"skipped {item.Id}: {TextUtil.Truncate(ex.Message, 160)}");
            return null;
        }

        var extension = ImageExtension(data);
        if (extension is null || data.Length < options.MinImageBytes)
        {
            Log.Warn($"skipped {item.Id}: not a JPEG, PNG or WebP picture of a usable size ({data.Length} bytes)");
            return null;
        }
        var path = Path.Combine(folder, name + extension);
        await File.WriteAllBytesAsync(path, data, ct);
        try
        {
            var probe = await ffmpeg.ProbeAsync(path, ct);
            if (probe.VideoCodec is not ("mjpeg" or "png" or "webp") || Math.Min(probe.Width, probe.Height) < options.MinImageShortSide)
            {
                Log.Warn($"skipped {item.Id}: {probe.VideoCodec} {probe.Width}x{probe.Height} is too small or not a picture");
                File.Delete(path);
                return null;
            }
            var hash = await ffmpeg.DifferenceHashAsync(path, ct);
            return item with { LocalPath = path, Width = probe.Width, Height = probe.Height, Hash = hash };
        }
        catch (ToolException ex)
        {
            Log.Warn($"skipped {item.Id}: ffprobe could not read it ({TextUtil.Truncate(ex.Message, 160)})");
            File.Delete(path);
            return null;
        }
    }

    internal static TimeSpan RateLimitWait(HttpResponseMessage response)
    {
        var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.Zero;
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values)
            && double.TryParse(values.First().Split(',')[0], System.Globalization.CultureInfo.InvariantCulture, out var reset))
            wait = TimeSpan.FromSeconds(Math.Max(wait.TotalSeconds, reset + 1));
        return wait < TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait;
    }

    /// <summary>The extension for the file's magic bytes, or null when it is no picture we accept.</summary>
    internal static string? ImageExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return null;
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ".jpg";
        if (data[..8].SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ".png";
        if (data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }

    /// <summary>True for an https URL on one of <see cref="FootageHosts"/>.</summary>
    internal static bool IsAllowedFootageUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && FootageHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Cuts a short silent clip out of long public-domain footage without downloading all of it: FFmpeg seeks inside the
/// remote file over HTTPS range requests. Several spots are tried and the best kept: bright enough, contrasty, moving,
/// and with no hard cut in it (raw newsreel reels jump between scenes).
/// </summary>
public sealed class ClipExtractor(Ffmpeg ffmpeg, VideoOptions video)
{
    /// <summary>Spots tried, as shares of the footage: the start often has a slate or leader, the end a tail.</summary>
    internal static readonly double[] Spots = [0.32, 0.5, 0.68];

    public async Task<MediaItem?> ExtractAsync(MediaItem item, string folder, double seconds, CancellationToken ct)
    {
        if (!MediaDownloader.IsAllowedFootageUrl(item.DownloadUrl))
        {
            Log.Warn($"skipped {item.Id}: footage URL is not https on an allowed host");
            return null;
        }
        Directory.CreateDirectory(folder);
        var length = item.DurationSeconds ?? 0;
        if (length < seconds + 4) return null;

        (string Path, double Start, double Score, ProbeInfo Probe)? best = null;
        var stem = TextUtil.Slug(item.Id, 30);
        foreach (var spot in Spots)
        {
            // A little longer than needed, so a hard cut near either end can be trimmed off.
            var take = seconds + 1.5;
            var start = Math.Round(Math.Clamp(length * spot, 2, length - take - 1), 2);
            var name = $"clip-{stem}-{(int)(spot * 100)}.mp4";
            var path = Path.Combine(folder, name);
            try
            {
                await ProcessRunner.RunCheckedAsync(ffmpeg.FfmpegPath,
                    ["-y", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "https,tls,tcp,crypto",
                     "-ss", Num(start), "-i", item.DownloadUrl, "-t", Num(take), "-an", "-sn", "-dn",
                     "-vf", $"fps={video.Fps},scale=-2:'min(ih,1080)'", "-c:v", "libx264", "-preset", "veryfast", "-crf", "14", "-pix_fmt", "yuv420p", name],
                    folder, TimeSpan.FromMinutes(4), ct);
                var probe = await ffmpeg.ProbeAsync(path, ct);
                if (probe.Width < 320 || probe.Duration < seconds) throw new ToolException($"clip too small: {probe}");
                var stats = await FrameStatsAsync(name, folder, ct);
                var (offset, score) = Judge(stats, video.Fps, seconds);
                Log.Info($"{item.Id} at {start:0}s: score {score:0.00}");
                if (score > 0 && (best is null || score > best.Value.Score))
                {
                    if (best is not null) File.Delete(best.Value.Path);
                    best = (path, start + offset, score, probe);
                    // Re-cut the exact window later from this file: keep its offset inside the take.
                    if (offset > 0) best = await TrimAsync(path, folder, offset, seconds, start, score, probe, ct);
                }
                else
                {
                    File.Delete(path);
                }
            }
            catch (ToolException ex)
            {
                Log.Warn($"{item.Id} at {start:0}s failed: {TextUtil.Truncate(ex.Message, 200)}");
                File.Delete(path);
            }
        }
        if (best is null) return null;
        var final = Path.Combine(folder, $"clip-{stem}.mp4");
        File.Move(best.Value.Path, final, overwrite: true);
        return item with { LocalPath = final, ClipStart = best.Value.Start, Width = best.Value.Probe.Width, Height = best.Value.Probe.Height };
    }

    private async Task<(string, double, double, ProbeInfo)> TrimAsync(string path, string folder, double offset, double seconds,
        double start, double score, ProbeInfo probe, CancellationToken ct)
    {
        var trimmed = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "-cut.mp4");
        await ProcessRunner.RunCheckedAsync(ffmpeg.FfmpegPath,
            ["-y", "-hide_banner", "-loglevel", "error", "-ss", Num(offset), "-i", Path.GetFileName(path), "-t", Num(seconds + 0.2),
             "-c:v", "libx264", "-preset", "veryfast", "-crf", "14", "-pix_fmt", "yuv420p", Path.GetFileName(trimmed)],
            folder, TimeSpan.FromMinutes(2), ct);
        File.Delete(path);
        return (trimmed, start + offset, score, probe);
    }

    internal sealed record FrameStat(double Brightness, double Contrast, double Motion);

    private async Task<IReadOnlyList<FrameStat>> FrameStatsAsync(string name, string folder, CancellationToken ct)
    {
        var statsFile = $"{Path.GetFileNameWithoutExtension(name)}.stats.txt";
        await ProcessRunner.RunCheckedAsync(ffmpeg.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-i", name, "-vf", $"signalstats,metadata=mode=print:file={statsFile}", "-f", "null", "-"],
            folder, TimeSpan.FromMinutes(2), ct);
        var path = Path.Combine(folder, statsFile);
        var stats = ParseStats(await File.ReadAllLinesAsync(path, ct));
        File.Delete(path);
        return stats;
    }

    internal static IReadOnlyList<FrameStat> ParseStats(IEnumerable<string> lines)
    {
        var frames = new List<FrameStat>();
        double avg = 0, low = 0, high = 0, dif = 0;
        var any = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("frame:", StringComparison.Ordinal))
            {
                if (any) frames.Add(new FrameStat(avg, high - low, dif));
                any = true;
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var value = double.TryParse(line[(eq + 1)..], CultureInfo.InvariantCulture, out var v) ? v : 0;
            switch (line[..eq])
            {
                case "lavfi.signalstats.YAVG": avg = value; break;
                case "lavfi.signalstats.YLOW": low = value; break;
                case "lavfi.signalstats.YHIGH": high = value; break;
                case "lavfi.signalstats.YDIF": dif = value; break;
            }
        }
        if (any) frames.Add(new FrameStat(avg, high - low, dif));
        return frames;
    }

    /// <summary>
    /// Where the longest stretch without a hard cut starts (seconds into the take), and how good it looks: 0 for
    /// black, white, flat or too short. Contrast counts most, then some movement.
    /// </summary>
    internal static (double Offset, double Score) Judge(IReadOnlyList<FrameStat> stats, int fps, double seconds)
    {
        if (stats.Count == 0) return (0, 0);
        var median = stats.Select(s => s.Motion).Order().ElementAt(stats.Count / 2);
        var cutThreshold = Math.Max(18, median * 4);
        // Runs between hard cuts; the first frame's YDIF is meaningless.
        var runs = new List<(int Start, int End)>();
        var runStart = 0;
        for (var i = 1; i < stats.Count; i++)
            if (stats[i].Motion > cutThreshold)
            {
                runs.Add((runStart, i));
                runStart = i;
            }
        runs.Add((runStart, stats.Count));
        var (start, end) = runs.MaxBy(r => r.End - r.Start);
        var needed = (int)Math.Ceiling(seconds * fps);
        if (end - start < needed) return (0, 0);
        var window = stats.Skip(start).Take(needed).ToList();
        var brightness = window.Average(s => s.Brightness);
        var contrast = window.Average(s => s.Contrast);
        var motion = window.Skip(1).DefaultIfEmpty(window[0]).Average(s => s.Motion);
        if (brightness < 30 || brightness > 225 || contrast < 45) return (0, 0);
        return ((double)start / fps, Math.Round(contrast / 255 + Math.Min(motion, 8) / 8 * 0.4, 3));
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
