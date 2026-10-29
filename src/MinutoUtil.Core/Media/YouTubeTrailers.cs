using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Core.Media;

/// <summary>
/// Official trailers of a game from YouTube, for games Steam does not sell (GTA VI) or videos named per run with
/// --youtube. yt-dlp (tools/pyproject.toml) searches and downloads; a search keeps only videos from an official channel:
/// one in media.youTubeChannels (Rockstar, PlayStation, IGN...) or a verified channel named after the game. Each video
/// track (no audio) is cut to media.maxTrailerSeconds into the run's media/trailers/. yt-dlp's answers are untrusted data:
/// ids are checked, the watch address is built here, and every file is probed before use.
/// </summary>
public sealed partial class YouTubeTrailers(Ffmpeg ffmpeg, MediaOptions options, string toolsDirectory)
{
    private const int SearchResults = 20;

    /// <summary>Titles that are about a trailer rather than the trailer: reactions, reviews, analysis, music.</summary>
    private static readonly string[] NotATrailer =
    [
        "reaction", "react", "reacts", "review", "breakdown", "analysis", "explained", "everything", "details", "theory",
        "podcast", "interview", "live", "livestream", "stream", "comparison", "vs", "soundtrack", "ost", "song", "music",
        "fan", "fanmade", "concept", "leak", "leaked", "mod", "remake", "recreated", "parody", "meme", "shorts",
    ];

    /// <summary>Searches YouTube for the game's official trailers.</summary>
    public async Task<IReadOnlyList<MediaItem>> SearchAsync(string game, string mediaFolder, CancellationToken ct)
    {
        var name = GameNames.Title(game);
        if (GameNames.Key(name).Length == 0) throw new MediaException("No game name to look up on YouTube.");
        var result = await YtDlpAsync(["--flat-playlist", "--dump-json", $"ytsearch{SearchResults}:{name} official trailer"], TimeSpan.FromMinutes(2), ct);
        var found = Pick(result.Split('\n').Select(ParseLine).OfType<JsonObject>(), name, options);
        if (found.Count == 0) throw new MediaException($"YouTube has no official trailer of \"{name}\" from a channel in media.youTubeChannels; name the videos with --youtube.");
        Log.Info($"trailers: YouTube has {found.Count} official video(s) of \"{name}\": {string.Join(", ", found.Take(options.Trailers).Select(v => v.Title))}");
        return await DownloadAllAsync(found, mediaFolder, ct);
    }

    /// <summary>Downloads the videos named per run (media.youTubeVideos), whoever posted them.</summary>
    public async Task<IReadOnlyList<MediaItem>> NamedAsync(IReadOnlyList<string> ids, string mediaFolder, CancellationToken ct)
    {
        var videos = new List<Video>();
        foreach (var id in ids)
        {
            var info = ParseLine(await YtDlpAsync(["--no-playlist", "--skip-download", "--dump-json", "--", Watch(id)], TimeSpan.FromMinutes(2), ct));
            videos.Add(Describe(info as JsonObject) ?? throw new MediaException($"YouTube gave no details for video {id}."));
        }
        return await DownloadAllAsync(videos, mediaFolder, ct);
    }

    /// <summary>
    /// The search results that are a game's official videos, in the search's order with titles saying "trailer" first.
    /// A reupload (another channel's video of the same length) is dropped, so no footage repeats.
    /// </summary>
    internal static List<Video> Pick(IEnumerable<JsonObject> results, string game, MediaOptions options)
    {
        var wanted = GameNames.Title(game);
        var allowed = options.YouTubeChannels.ToHashSet(StringComparer.Ordinal);
        var picked = new List<Video>();
        foreach (var video in results.Select(Describe).OfType<Video>())
        {
            var officialChannel = allowed.Contains(video.ChannelId) || video.Verified && GameNames.Names(video.Channel, wanted);
            var words = Words().Matches(TextUtil.Fold(video.Title)).Select(m => m.Value).ToHashSet();
            if (!officialChannel || !GameNames.Names(video.Title, wanted) || NotATrailer.Any(words.Contains)) continue;
            if (video.Duration is < 20 or > 3600) continue;
            if (picked.Any(p => p.ChannelId != video.ChannelId && Math.Abs(p.Duration - video.Duration) <= 1)) continue;
            picked.Add(video);
        }
        return picked.OrderByDescending(v => TextUtil.Fold(v.Title).Contains("trailer", StringComparison.Ordinal)).ToList();
    }

    /// <summary>A YouTube video id from an id, a watch address or a youtu.be link; null when it is none of those.</summary>
    public static string? VideoId(string text)
    {
        text = text.Trim();
        if (Id().IsMatch(text)) return text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        var host = uri.Host.StartsWith("www.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host;
        var candidate = host switch
        {
            "youtu.be" => uri.AbsolutePath.Trim('/'),
            "youtube.com" or "m.youtube.com" when uri.AbsolutePath == "/watch" => QueryValue(uri.Query, "v"),
            "youtube.com" or "m.youtube.com" when uri.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal) => uri.AbsolutePath[8..].Trim('/'),
            _ => null,
        };
        return candidate is not null && Id().IsMatch(candidate) ? candidate : null;
    }

    internal sealed record Video(string Id, string Title, string Channel, string ChannelId, bool Verified, double Duration);

    private static Video? Describe(JsonObject? item)
    {
        var id = Text(item, "id");
        if (item is null || id is null || !Id().IsMatch(id) || item["live_status"]?.ToString() is "is_live" or "is_upcoming") return null;
        return new Video(id, SteamTrailers.Clean(Text(item, "title") ?? id), SteamTrailers.Clean(Text(item, "channel") ?? Text(item, "uploader") ?? ""),
            Text(item, "channel_id") ?? "", item["channel_is_verified"]?.GetValueKind() == JsonValueKind.True,
            item["duration"]?.GetValueKind() == JsonValueKind.Number ? item["duration"]!.GetValue<double>() : 0);
    }

    private async Task<IReadOnlyList<MediaItem>> DownloadAllAsync(IReadOnlyList<Video> videos, string mediaFolder, CancellationToken ct)
    {
        var folder = Path.Combine(mediaFolder, "trailers");
        Directory.CreateDirectory(folder);
        var trailers = new List<MediaItem>();
        foreach (var video in videos)
        {
            if (trailers.Count >= options.Trailers) break;
            var path = Path.Combine(folder, $"youtube-{video.Id}.mp4");
            var partial = Path.Combine(folder, $"youtube-{video.Id}.partial.mp4");
            try
            {
                // The best H.264 video track up to 1080p (OpenCV in trailer.py reads it everywhere), else any up to 1080p.
                await YtDlpAsync(["--no-playlist", "--no-part", "--max-filesize", "600M", "-f", "bv*[height<=1080][vcodec^=avc1]/bv*[height<=1080]",
                    "-o", Path.GetFullPath(partial), "--", Watch(video.Id)], TimeSpan.FromMinutes(10), ct);
                await ProcessRunner.RunCheckedAsync(ffmpeg.FfmpegPath,
                    ["-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-i", Path.GetFullPath(partial), "-map", "0:v:0", "-c", "copy",
                     "-t", options.MaxTrailerSeconds.ToString(CultureInfo.InvariantCulture), "-movflags", "+faststart", Path.GetFullPath(path)],
                    folder, TimeSpan.FromMinutes(5), ct);
                var probe = await ffmpeg.ProbeAsync(path, ct);
                if (probe.Height < 720 || probe.Duration < 20)
                {
                    Log.Warn($"trailers: skipped {video.Id}, {probe.Width}x{probe.Height} and {probe.Duration:0}s is too small or short");
                    File.Delete(path);
                    continue;
                }
                trailers.Add(new MediaItem
                {
                    Id = $"youtube-{video.Id}",
                    Kind = MediaKind.Video,
                    Source = "YouTube",
                    Title = video.Title,
                    Author = video.Channel.Length > 0 ? video.Channel : "YouTube",
                    License = "Trailer oficial",
                    SourceUrl = Watch(video.Id),
                    DownloadUrl = Watch(video.Id),
                    Width = probe.Width,
                    Height = probe.Height,
                    DurationSeconds = probe.Duration,
                    LocalPath = path,
                });
                Log.Info($"trailers: {video.Title} ({video.Channel}), {probe.Width}x{probe.Height}, {probe.Duration:0}s");
            }
            catch (Exception ex) when (ex is MediaException or ToolException)
            {
                Log.Warn($"trailers: skipped {video.Id}: {TextUtil.Truncate(ex.Message, 200)}");
                if (File.Exists(path)) File.Delete(path);
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
        }
        if (trailers.Count == 0) throw new MediaException("None of the YouTube trailers could be downloaded.");
        return trailers;
    }

    /// <summary>yt-dlp from the tools' environment, ignoring any user config; Node solves YouTube's player challenges.</summary>
    private async Task<string> YtDlpAsync(IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var result = await ProcessRunner.RunCheckedAsync("uv",
            ["run", "--quiet", "yt-dlp", "--ignore-config", "--no-warnings", "--no-progress", "--js-runtimes", "node", .. arguments],
            toolsDirectory, timeout, ct);
        return result.StandardOutput;
    }

    private static string Watch(string id) => $"https://www.youtube.com/watch?v={id}";

    private static JsonNode? ParseLine(string line)
    {
        try { return line.Trim().Length > 0 ? JsonNode.Parse(line) : null; }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonObject? item, string key) =>
        item?[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string? QueryValue(string query, string key) =>
        query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == key)?[1];

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex Id();

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Words();
}
