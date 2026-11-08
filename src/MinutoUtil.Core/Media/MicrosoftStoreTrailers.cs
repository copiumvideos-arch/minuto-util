using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Core.Media;

/// <summary>
/// The official trailers of a game from its Microsoft Store (Xbox and PC) page, a second store besides Steam that the
/// cloud runners can reach (YouTube refuses them). The game is found by name in the store's catalog search, the
/// trailers come from the store's product API (the one the Store app reads), and FFmpeg copies the best video track up
/// to 1080p of each DASH manifest into the run's media/trailers/. The store's answers are untrusted data: only https
/// manifests on Microsoft's trailer host are read, the manifest is parsed with DTDs off, and every file is probed.
/// </summary>
public sealed class MicrosoftStoreTrailers(HttpClient http, Ffmpeg ffmpeg, MediaOptions options)
{
    private const string Catalog = "https://displaycatalog.mp.microsoft.com/v7.0/productFamilies/autosuggest";
    private const string Product = "https://storeedgefd.dsx.mp.microsoft.com/v9.0/products";
    private const int MaxJsonBytes = 4_000_000;

    /// <summary>
    /// Downloads up to media.trailers of the game's store trailers, skipping any with the title or the length of one in
    /// <paramref name="already"/> (the same trailer is often on Steam too). Empty when none could be used.
    /// </summary>
    public async Task<IReadOnlyList<MediaItem>> GatherAsync(string game, IReadOnlyList<MediaItem> already, string mediaFolder, CancellationToken ct)
    {
        var (id, name) = await FindProductAsync(game, ct);
        var payload = (await GetJsonAsync($"{Product}/{id}?market=BR&locale=pt-BR&deviceFamily=Windows.Desktop", ct))?["Payload"] as JsonObject
                      ?? throw new MediaException($"The Microsoft Store has no page for {name} ({id}).");
        var studio = SteamTrailers.Clean(payload["DeveloperName"]?.ToString() ?? payload["PublisherName"]?.ToString() ?? name);
        var trailers = (payload["Trailers"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(t => (Title: SteamTrailers.Clean(t["Title"]?.ToString() ?? name), Purpose: t["VideoPurpose"]?.ToString() ?? "",
                Order: t["SortOrder"]?.GetValueKind() == JsonValueKind.Number ? t["SortOrder"]!.GetValue<int>() : 99, Url: Manifest(t["Url"]?.ToString())))
            .Where(t => t.Url is not null)
            .OrderByDescending(t => t.Purpose.Equals("HeroTrailer", StringComparison.OrdinalIgnoreCase)).ThenBy(t => t.Order)
            .ToList();
        if (trailers.Count == 0) throw new MediaException($"{name} has no trailers on the Microsoft Store.");

        var folder = Path.Combine(mediaFolder, "trailers");
        Directory.CreateDirectory(folder);
        var seen = already.Select(t => GameNames.Key(t.Title)).ToHashSet(StringComparer.Ordinal);
        var found = new List<MediaItem>();
        for (var n = 0; n < trailers.Count && found.Count < options.Trailers; n++)
        {
            var trailer = trailers[n];
            if (!seen.Add(GameNames.Key(trailer.Title))) continue;
            var path = Path.Combine(folder, $"microsoft-{id}-{n + 1}.mp4");
            try
            {
                await DownloadAsync(trailer.Url!, path, ct);
                var probe = await ffmpeg.ProbeAsync(path, ct);
                if (probe.Height < 720 || probe.Duration < 20)
                {
                    Log.Warn($"trailers: skipped {trailer.Title}, {probe.Width}x{probe.Height} and {probe.Duration:0}s is too small or short");
                    File.Delete(path);
                    continue;
                }
                // Another title for a trailer already had: the same length within a second.
                if (already.Any(t => t.DurationSeconds is { } d && Math.Abs(d - probe.Duration) <= 1.0))
                {
                    Log.Info($"trailers: skipped {trailer.Title} (Microsoft Store), the same length as a trailer already had");
                    File.Delete(path);
                    continue;
                }
                found.Add(new MediaItem
                {
                    Id = $"microsoft-{id}-{n + 1}",
                    Kind = MediaKind.Video,
                    Source = "Microsoft Store",
                    Title = trailer.Title,
                    Author = studio,
                    License = "Trailer oficial",
                    SourceUrl = $"https://apps.microsoft.com/detail/{id}?gl=BR",
                    DownloadUrl = trailer.Url!.ToString(),
                    Width = probe.Width,
                    Height = probe.Height,
                    DurationSeconds = probe.Duration,
                    LocalPath = path,
                });
                Log.Info($"trailers: {trailer.Title} (Microsoft Store), {probe.Width}x{probe.Height}, {probe.Duration:0}s");
            }
            catch (Exception ex) when (ex is MediaException or ToolException or HttpRequestException or XmlException)
            {
                Log.Warn($"trailers: skipped {trailer.Title}: {TextUtil.Truncate(ex.Message, 200)}");
                if (File.Exists(path)) File.Delete(path);
            }
        }
        return found;
    }

    /// <summary>
    /// The store product whose title is the game's name (or that name with edition words, like Steam), else refused:
    /// another game's trailers would contradict the narration.
    /// </summary>
    internal async Task<(string Id, string Name)> FindProductAsync(string game, CancellationToken ct)
    {
        var title = GameNames.Title(game);
        if (GameNames.Key(title).Length == 0) throw new MediaException("No game name to look up on the Microsoft Store.");
        var found = await GetJsonAsync($"{Catalog}?market=BR&languages=pt-br&query={Uri.EscapeDataString(title)}&productFamilyNames=Games", ct);
        var products = (found?["Results"] as JsonArray ?? []).OfType<JsonObject>()
            .SelectMany(r => (r["Products"] as JsonArray ?? []).OfType<JsonObject>())
            .Select(p => (Id: p["ProductId"]?.ToString() ?? "", Name: SteamTrailers.Clean(p["Title"]?.ToString() ?? "")))
            .Where(p => ProductId(p.Id))
            .ToList();
        var match = products.FirstOrDefault(p => SteamTrailers.SameGame(p.Name, title));
        if (match.Id is { Length: > 0 }) return match;
        throw new MediaException($"The Microsoft Store has no game named \"{title}\" (found: {string.Join(", ", products.Take(3).Select(p => p.Name))}).");
    }

    /// <summary>The best video track up to 1080p of a DASH manifest, video only, cut to media.maxTrailerSeconds.</summary>
    private async Task DownloadAsync(Uri manifest, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(manifest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var track = BestVideoTrack(await SafeHttp.ReadLimitedAsync(response, 512_000, ct));
        var partial = Path.ChangeExtension(path, ".partial.mp4");
        try
        {
            await ProcessRunner.RunCheckedAsync(ffmpeg.FfmpegPath,
                ["-y", "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "https,tls,tcp,crypto,data",
                 "-i", manifest.ToString(), "-map", $"0:v:{track.ToString(CultureInfo.InvariantCulture)}", "-c", "copy",
                 "-t", options.MaxTrailerSeconds.ToString(CultureInfo.InvariantCulture), "-movflags", "+faststart", Path.GetFullPath(partial)],
                Path.GetDirectoryName(Path.GetFullPath(path))!, TimeSpan.FromMinutes(10), ct);
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    /// <summary>
    /// The position, among the manifest's video tracks (FFmpeg's 0:v:N), of the one with the most lines up to 1080 and
    /// then the highest bitrate.
    /// </summary>
    internal static int BestVideoTrack(string mpd)
    {
        using var reader = XmlReader.Create(new StringReader(mpd), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var videos = XDocument.Load(reader).Descendants().Where(e => e.Name.LocalName == "Representation")
            .Where(r => (r.Attribute("mimeType")?.Value ?? r.Parent?.Attribute("mimeType")?.Value ?? r.Parent?.Attribute("contentType")?.Value ?? "")
                        .StartsWith("video", StringComparison.OrdinalIgnoreCase) || r.Attribute("height") is not null)
            .Select((r, i) => (Index: i, Height: Number(r.Attribute("height")?.Value), Bandwidth: Number(r.Attribute("bandwidth")?.Value)))
            .ToList();
        var best = videos.Where(v => v.Height is > 0 and <= 1080).OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).ToList();
        if (best.Count == 0) throw new MediaException("The trailer's manifest lists no video track up to 1080p.");
        return best[0].Index;
    }

    private static long Number(string? text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>Only https DASH manifests on Microsoft's trailer host are followed.</summary>
    internal static Uri? Manifest(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.EndsWith(".xboxservices.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;

    private static bool ProductId(string id) => id.Length is >= 8 and <= 16 && id.All(char.IsAsciiLetterOrDigit);

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new MediaException($"The Microsoft Store answered {(int)response.StatusCode} for {new Uri(url).AbsolutePath}.");
        return JsonNode.Parse(await SafeHttp.ReadLimitedAsync(response, MaxJsonBytes, ct));
    }
}
