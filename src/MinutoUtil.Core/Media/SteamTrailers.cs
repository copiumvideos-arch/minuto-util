using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Core.Media;

/// <summary>Footage could not be found or used; the message says why, for the run log and Telegram.</summary>
public sealed class MediaException(string message) : Exception(message);

/// <summary>
/// The official trailers of a game from its Steam store page: the footage of a channel with media.footage "trailers".
/// The game is found by the name of its Wikipedia article (or media.steamAppId), each trailer's HLS stream is read at
/// its best resolution up to 1080p, and FFmpeg copies the video track (no audio) into the run's media/trailers/. The
/// store's answers are untrusted data: the streams must be https on Steam's video host, and every file is probed
/// before use. The list is saved as trailers.json and reused by a re-render.
/// </summary>
public sealed partial class SteamTrailers(HttpClient http, Ffmpeg ffmpeg, MediaOptions options)
{
    private const string Store = "https://store.steampowered.com";
    private const int MaxJsonBytes = 4_000_000;

    public async Task<IReadOnlyList<MediaItem>> GatherAsync(string game, int? appId, string mediaFolder, CancellationToken ct)
    {
        var folder = Path.Combine(mediaFolder, "trailers");
        var manifestPath = Path.Combine(mediaFolder, "trailers.json");
        if (File.Exists(manifestPath))
        {
            var saved = JsonSerializer.Deserialize<List<MediaItem>>(await File.ReadAllTextAsync(manifestPath, ct), Json.Options) ?? [];
            if (saved.Count > 0 && saved.All(t => File.Exists(t.LocalPath)))
            {
                Log.Info($"trailers: reused {saved.Count} from {manifestPath}");
                return saved;
            }
        }

        var app = appId ?? await FindAppAsync(game, ct);
        var details = await GetJsonAsync($"{Store}/api/appdetails?appids={app}&l=english", ct);
        var data = details?[app.ToString(CultureInfo.InvariantCulture)]?["data"]
                   ?? throw new MediaException($"Steam has no store page for app {app}.");
        var name = Clean(data["name"]?.GetValue<string>() ?? game);
        var studio = string.Join(", ", (data["developers"] as JsonArray ?? data["publishers"] as JsonArray ?? [])
            .Select(d => Clean(d?.GetValue<string>() ?? "")).Where(d => d.Length > 0));
        // The store's highlighted trailers first, then the rest in the store's order.
        var movies = (data["movies"] as JsonArray ?? []).OfType<JsonObject>()
            .OrderByDescending(m => m["highlight"]?.GetValue<bool>() == true).ToList();
        if (movies.Count == 0) throw new MediaException($"{name} (Steam app {app}) has no trailers on its store page.");

        Directory.CreateDirectory(folder);
        var trailers = new List<MediaItem>();
        foreach (var movie in movies)
        {
            if (trailers.Count >= options.Trailers) break;
            var id = movie["id"]?.ToString() ?? "";
            var master = movie["hls_h264"]?.GetValue<string>();
            if (!NumericId().IsMatch(id) || master is null) continue;
            var path = Path.Combine(folder, $"steam-{id}.mp4");
            try
            {
                var stream = await BestStreamAsync(master, ct);
                await DownloadAsync(stream, path, ct);
                var probe = await ffmpeg.ProbeAsync(path, ct);
                if (probe.Height < 720 || probe.Duration < 20)
                {
                    Log.Warn($"trailers: skipped {id}, {probe.Width}x{probe.Height} and {probe.Duration:0}s is too small or short");
                    File.Delete(path);
                    continue;
                }
                trailers.Add(new MediaItem
                {
                    Id = $"steam-{id}",
                    Kind = MediaKind.Video,
                    Source = "Steam",
                    Title = Clean(movie["name"]?.GetValue<string>() ?? name),
                    Author = studio.Length > 0 ? studio : name,
                    License = "Trailer oficial",
                    SourceUrl = $"{Store}/app/{app}/",
                    DownloadUrl = stream.ToString(),
                    Width = probe.Width,
                    Height = probe.Height,
                    DurationSeconds = probe.Duration,
                    LocalPath = path,
                });
                Log.Info($"trailers: {trailers[^1].Title}, {probe.Width}x{probe.Height}, {probe.Duration:0}s");
            }
            catch (Exception ex) when (ex is MediaException or ToolException or HttpRequestException or JsonException)
            {
                Log.Warn($"trailers: skipped {id}: {TextUtil.Truncate(ex.Message, 200)}");
                if (File.Exists(path)) File.Delete(path);
            }
        }
        if (trailers.Count == 0) throw new MediaException($"None of the trailers of {name} (Steam app {app}) could be downloaded.");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(trailers, Json.Options), ct);
        return trailers;
    }

    /// <summary>
    /// The Steam app of a game by name: the search result whose name is the game's name, or else the first one that
    /// starts with it (a later edition, "The Witcher 3: Wild Hunt - Remastered"), so "Hades" never finds "Hades II".
    /// Anything else is refused rather than guessed, since a wrong game's trailers would contradict the narration;
    /// --steam-app names the app then.
    /// </summary>
    internal async Task<int> FindAppAsync(string game, CancellationToken ct)
    {
        var title = WithoutQualifier().Replace(game, "").Trim();
        var wanted = Key(title);
        if (wanted.Length == 0) throw new MediaException("No game name to look up on Steam.");
        var found = await GetJsonAsync($"{Store}/api/storesearch/?term={Uri.EscapeDataString(title)}&l=english&cc=US", ct);
        var items = (found?["items"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(i => i["type"]?.GetValue<string>() == "app" && i["id"] is not null)
            .Select(i => (Id: i["id"]!.GetValue<int>(), Name: Clean(i["name"]?.GetValue<string>() ?? ""))).ToList();
        var match = items.Where(i => Key(i.Name) == wanted).Concat(items.Where(i => Key(i.Name).StartsWith(wanted, StringComparison.Ordinal)))
            .FirstOrDefault();
        if (match.Id > 0)
        {
            Log.Info($"trailers: \"{game}\" is Steam app {match.Id} ({match.Name})");
            return match.Id;
        }
        throw new MediaException($"Steam has no game named \"{game}\" (found: {string.Join(", ", items.Take(3).Select(i => i.Name))}); " +
                                 "set its app id with --steam-app.");
    }

    /// <summary>The variant with the most lines up to 1080, from an HLS master playlist on Steam's video host.</summary>
    internal async Task<Uri> BestStreamAsync(string masterUrl, CancellationToken ct)
    {
        var master = SteamVideo(masterUrl);
        using var response = await http.GetAsync(master, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var lines = (await SafeHttp.ReadLimitedAsync(response, 256_000, ct)).Split('\n').Select(l => l.Trim()).ToList();
        var best = (Height: 0, Uri: (Uri?)null);
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            var match = StreamResolution().Match(lines[i]);
            if (!match.Success || lines[i + 1].Length == 0 || lines[i + 1].StartsWith('#')) continue;
            var height = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (height <= 1080 && height > best.Height) best = (height, SteamVideo(new Uri(master, lines[i + 1]).ToString()));
        }
        return best.Uri ?? throw new MediaException("The trailer's playlist lists no video stream.");
    }

    private async Task DownloadAsync(Uri stream, string path, CancellationToken ct)
    {
        var partial = Path.ChangeExtension(path, ".partial.mp4");
        // Video only, copied as it is; a long trailer is cut, since a short never needs more.
        await ProcessRunner.RunCheckedAsync(ffmpeg.FfmpegPath,
            ["-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "https,tls,tcp,crypto,hls",
             "-i", stream.ToString(), "-map", "0:v:0", "-c", "copy", "-t", options.MaxTrailerSeconds.ToString(CultureInfo.InvariantCulture),
             "-movflags", "+faststart", Path.GetFullPath(partial)],
            Path.GetDirectoryName(Path.GetFullPath(path))!, TimeSpan.FromMinutes(10), ct);
        File.Move(partial, path, overwrite: true);
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new MediaException($"Steam answered {(int)response.StatusCode} for {new Uri(url).AbsolutePath}.");
        return JsonNode.Parse(await SafeHttp.ReadLimitedAsync(response, MaxJsonBytes, ct));
    }

    /// <summary>Only https URLs on Steam's own video host are followed.</summary>
    private static Uri SteamVideo(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "steamstatic.com" || uri.Host.EndsWith(".steamstatic.com", StringComparison.Ordinal))
            ? uri
            : throw new MediaException($"Not a Steam video address: {TextUtil.Truncate(url, 80)}");

    /// <summary>
    /// Steam sends some names with their UTF-8 read as Windows-1252 ("â€”" for a dash); those are put back, and a dash
    /// becomes a hyphen, since the copy never has dashes.
    /// </summary>
    internal static string Clean(string text)
    {
        foreach (var (bad, good) in new[] { ("â€”", "-"), ("â€“", "-"), ("â€™", "'"), ("â„¢", "™"), ("Â®", "®"), ("Â©", "©"), ("—", "-"), ("–", "-") })
            text = text.Replace(bad, good);
        return text.Trim();
    }

    /// <summary>Letters and digits only, folded: "The Witcher 3: Wild Hunt" and "the witcher 3 wild hunt" match.</summary>
    private static string Key(string text) => NotAlphanumeric().Replace(TextUtil.Fold(Clean(text)), "");

    /// <summary>Wikipedia's disambiguation in a title: "Hades (video game)", "God of War (2018 video game)".</summary>
    [GeneratedRegex(@"\s*\((\d{4} )?(video ?game|jogo eletrônico|jogo|game)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WithoutQualifier();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NotAlphanumeric();

    [GeneratedRegex(@"^#EXT-X-STREAM-INF:.*RESOLUTION=\d+x(\d+)")]
    private static partial Regex StreamResolution();

    [GeneratedRegex("^[0-9]{1,12}$")]
    private static partial Regex NumericId();
}
