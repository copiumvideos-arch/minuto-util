using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinutoUtil.Core.Video;

/// <summary>What ffprobe says about a file: the first video stream, whether there is audio, and the length.</summary>
public sealed record ProbeInfo(string VideoCodec, int Width, int Height, bool HasAudio, string AudioCodec, double Duration, double Fps, int Frames)
{
    public override string ToString() =>
        $"{VideoCodec} {Width}x{Height} {Fps:0.##} fps, {(HasAudio ? AudioCodec : "no audio")}, {Duration:0.00}s";
}

/// <summary>Small FFmpeg and ffprobe jobs. Arguments go through ArgumentList (see <see cref="ProcessRunner"/>), never a shell.</summary>
public sealed partial class Ffmpeg(VideoOptions options)
{
    public string FfmpegPath => options.FfmpegPath;

    public async Task<ProbeInfo> ProbeAsync(string path, CancellationToken ct)
    {
        var result = await ProcessRunner.RunCheckedAsync(options.FfprobePath,
            ["-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height,avg_frame_rate,nb_frames:format=duration", "-of", "json", path],
            Path.GetDirectoryName(Path.GetFullPath(path))!, TimeSpan.FromSeconds(60), ct);
        return ParseProbe(result.StandardOutput);
    }

    internal static ProbeInfo ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        string videoCodec = "", audioCodec = "";
        int width = 0, height = 0, frames = 0;
        double fps = 0;
        var hasAudio = false;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : "";
                var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                if (type == "video" && videoCodec.Length == 0)
                {
                    videoCodec = codec;
                    width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    fps = Rate(stream.TryGetProperty("avg_frame_rate", out var r) ? r.GetString() : null);
                    if (stream.TryGetProperty("nb_frames", out var n) && int.TryParse(n.GetString(), out var count)) frames = count;
                }
                else if (type == "audio")
                {
                    hasAudio = true;
                    audioCodec = codec;
                }
            }
        var duration = 0.0;
        if (doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out duration);
        return new ProbeInfo(videoCodec, width, height, hasAudio, audioCodec, duration, fps, frames);
    }

    private static double Rate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var parts = text.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var den) && den > 0)
            return n / den;
        return double.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>Integrated loudness (EBU R128) in LUFS, or null when FFmpeg reports none (silence).</summary>
    public async Task<double?> LoudnessAsync(string path, CancellationToken ct)
    {
        var result = await ProcessRunner.RunCheckedAsync(options.FfmpegPath,
            ["-hide_banner", "-nostats", "-i", path, "-af", "ebur128", "-f", "null", "-"],
            Path.GetDirectoryName(Path.GetFullPath(path))!, TimeSpan.FromMinutes(5), ct);
        return ParseIntegratedLoudness(result.StandardError);
    }

    [GeneratedRegex(@"Integrated loudness:\s*I:\s*(-?\d+(?:\.\d+)?)\s*LUFS", RegexOptions.Singleline)]
    private static partial Regex Integrated();

    internal static double? ParseIntegratedLoudness(string stderr)
    {
        var match = Integrated().Match(stderr);
        if (!match.Success) return null;
        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return value <= -70 ? null : value;
    }

    /// <summary>
    /// 64-bit difference hash of a picture (FFmpeg shrinks it to 9x8 gray): pictures a few bits apart are the same
    /// photo in another crop, scan or size.
    /// </summary>
    public async Task<ulong> DifferenceHashAsync(string path, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".hash-{Guid.NewGuid():N}.gray");
        try
        {
            await ProcessRunner.RunCheckedAsync(options.FfmpegPath,
                ["-y", "-hide_banner", "-loglevel", "error", "-i", path, "-frames:v", "1", "-vf", "scale=9:8:flags=area,format=gray", "-f", "rawvideo", temp],
                Path.GetDirectoryName(Path.GetFullPath(path))!, TimeSpan.FromSeconds(60), ct);
            var pixels = await File.ReadAllBytesAsync(temp, ct);
            if (pixels.Length < 72) throw new ToolException($"Could not hash {Path.GetFileName(path)}.");
            ulong hash = 0;
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++)
                    hash = hash << 1 | (pixels[y * 9 + x] > pixels[y * 9 + x + 1] ? 1UL : 0UL);
            return hash;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public static int HashDistance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}
