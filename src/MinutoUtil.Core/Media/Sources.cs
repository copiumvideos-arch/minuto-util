using System.Text.Json;

namespace MinutoUtil.Core.Media;

public interface IMediaSource
{
    string Name { get; }

    /// <summary>Allowed-license results for a query, best first. Never throws for a failed request: logs and returns none.</summary>
    Task<IReadOnlyList<MediaItem>> SearchAsync(string query, MediaKind kind, int limit, CancellationToken ct);
}

/// <summary>Shared plumbing: GET a JSON API with a size cap, and turn every failure into a logged empty result.</summary>
public abstract class JsonMediaSource(HttpClient http, MediaOptions options) : IMediaSource
{
    private const int MaxJsonBytes = 8_000_000;

    protected MediaOptions Options => options;

    public abstract string Name { get; }

    /// <summary>Set after a source refuses us (a bot challenge, an outage), so the run stops asking it.</summary>
    protected bool Disabled { get; set; }

    public async Task<IReadOnlyList<MediaItem>> SearchAsync(string query, MediaKind kind, int limit, CancellationToken ct)
    {
        if (Disabled) return [];
        try
        {
            var items = await SearchCoreAsync(query, kind, limit, ct);
            return items.Where(i => !MediaScoring.IsBlocked($"{i.Title} {i.Query}", options.BlockedWords)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or KeyNotFoundException
                                       or InvalidOperationException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"{Name} search \"{query}\" failed: {TextUtil.Truncate(ex.Message, 200)}");
            return [];
        }
    }

    protected abstract Task<IReadOnlyList<MediaItem>> SearchCoreAsync(string query, MediaKind kind, int limit, CancellationToken ct);

    /// <summary>Shortest time between two requests to this source: Wikimedia and the Archive ask bots to go slowly.</summary>
    protected virtual TimeSpan MinInterval => TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _last = DateTime.MinValue;

    /// <summary>Longest Retry-After honored; a source that wants more is skipped for the run.</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    protected async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await ThrottledGetAsync(uri, ct);
            if (response.IsSuccessStatusCode)
            {
                var body = await SafeHttp.ReadLimitedAsync(response, MaxJsonBytes, ct);
                return JsonDocument.Parse(body);
            }
            var status = (int)response.StatusCode;
            // A rate limit gets one wait of the time the server asks for; a second one, or a bot wall (403), ends this
            // source for the run rather than hammering it.
            if (status is 429 or 503 && attempt == 1)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                if (wait <= MaxRetryAfter)
                {
                    Log.Warn($"{uri.Host} returned {status}, waiting {wait.TotalSeconds:0}s once");
                    await Task.Delay(wait, ct);
                    continue;
                }
            }
            if (status is 403 or 429 or 503)
            {
                Disabled = true;
                throw new HttpRequestException($"{uri.Host} returned {status}; not asking it again this run");
            }
            throw new HttpRequestException($"{uri.Host} returned {status}");
        }
    }

    private async Task<HttpResponseMessage> ThrottledGetAsync(Uri uri, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _last + MinInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _last = DateTime.UtcNow;
            return await http.GetAsync(uri, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    protected static string Query(IDictionary<string, string> parameters) =>
        string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    protected static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Array => string.Join("; ", value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())),
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => value.ToString(),
            }
            : "";
}

/// <summary>
/// Wikimedia Commons (MediaWiki API, file namespace). The license comes from the file's own extmetadata
/// (LicenseShortName, LicenseUrl), the author from Artist, so every result can be credited.
/// </summary>
public sealed class CommonsSource(HttpClient http, MediaOptions options) : JsonMediaSource(http, options)
{
    public override string Name => "Wikimedia Commons";

    protected override async Task<IReadOnlyList<MediaItem>> SearchCoreAsync(string query, MediaKind kind, int limit, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["action"] = "query",
            ["format"] = "json",
            ["formatversion"] = "2",
            ["generator"] = "search",
            ["gsrnamespace"] = "6",
            ["gsrlimit"] = limit.ToString(),
            ["gsrsearch"] = kind == MediaKind.Video ? $"{query} filetype:video" : $"{query} filetype:bitmap",
            ["prop"] = "imageinfo",
            ["iiprop"] = "url|size|mime|extmetadata",
            ["iiextmetadatafilter"] = "LicenseShortName|LicenseUrl|Artist|Credit|DateTimeOriginal|ObjectName",
        };
        // Thumbnails are JPEGs whatever the original is (TIFF, huge PNG), at a size the video needs.
        // 2560 is one of Wikimedia's standard thumbnail widths (others are generated on demand and rate limited sooner).
        if (kind == MediaKind.Image) parameters["iiurlwidth"] = "2560";
        using var doc = await GetJsonAsync(new Uri("https://commons.wikimedia.org/w/api.php?" + Query(parameters)), ct);
        return Parse(doc.RootElement, query, kind);
    }

    /// <summary>Thumbnail widths Wikimedia keeps ready (https://w.wiki/GHai); other sizes and originals get rate limited.</summary>
    private static readonly int[] StandardWidths = [3840, 1920, 1280, 960];

    /// <summary>
    /// A standard-size thumbnail of an original that the API returned unscaled (it was smaller than the size asked).
    /// On 2026-10-27 upload.wikimedia.org answered every download of an original from this PC's IPv4 address with
    /// 429 and Retry-After 600, while thumbnails in the standard sizes came through. Null when the original is too
    /// small for any of them.
    /// </summary>
    internal static string? StandardThumbnail(string originalUrl, int width)
    {
        const string marker = "/wikipedia/commons/";
        var at = originalUrl.IndexOf(marker, StringComparison.Ordinal);
        var size = StandardWidths.FirstOrDefault(w => w < width);
        if (at < 0 || size == 0) return null;
        var path = originalUrl[(at + marker.Length)..].Split('?')[0];
        var name = path[(path.LastIndexOf('/') + 1)..];
        return $"{originalUrl[..(at + marker.Length)]}thumb/{path}/{size}px-{name}";
    }

    internal static IReadOnlyList<MediaItem> Parse(JsonElement root, string query, MediaKind kind)
    {
        if (!root.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages)) return [];
        var items = new List<(int Index, MediaItem Item)>();
        foreach (var page in pages.EnumerateArray())
        {
            if (!page.TryGetProperty("imageinfo", out var infos) || infos.GetArrayLength() == 0) continue;
            var info = infos[0];
            var mime = Str(info, "mime");
            var isVideo = mime.StartsWith("video/", StringComparison.Ordinal) || mime == "application/ogg";
            if (kind == MediaKind.Video != isVideo) continue;
            if (kind == MediaKind.Image && mime is not ("image/jpeg" or "image/png" or "image/tiff" or "image/webp")) continue;
            var meta = info.TryGetProperty("extmetadata", out var m) ? m : default;
            string Meta(string name) => meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(name, out var f) ? Str(f, "value") : "";

            var license = LicensePolicy.Classify(Meta("LicenseShortName"), Meta("LicenseUrl"));
            if (license is null) continue;
            var author = TextUtil.StripHtml(Meta("Artist"));
            if (author.Length == 0) author = TextUtil.StripHtml(Meta("Credit"));
            var title = Str(page, "title");
            var name = title.StartsWith("File:", StringComparison.Ordinal) ? title[5..] : title;
            var width = info.TryGetProperty("width", out var wp) ? wp.GetInt32() : 0;
            var download = Str(info, "url");
            if (kind == MediaKind.Image)
            {
                download = Str(info, "thumburl") is { Length: > 0 } thumb && thumb.Contains("/thumb/", StringComparison.Ordinal)
                    ? thumb
                    : StandardThumbnail(Str(info, "url"), width);
                if (download is null) continue;
            }
            items.Add((page.TryGetProperty("index", out var index) ? index.GetInt32() : items.Count, new MediaItem
            {
                Id = "commons:" + name,
                Kind = kind,
                Source = "Wikimedia Commons",
                Title = Path.GetFileNameWithoutExtension(name),
                Author = author.Length == 0 ? "Autor desconhecido" : TextUtil.Truncate(author, 160),
                License = license,
                SourceUrl = Str(info, "descriptionurl"),
                DownloadUrl = download,
                Width = width,
                Height = info.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                DurationSeconds = info.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null,
                Year = MediaScoring.YearOf(Meta("DateTimeOriginal")) ?? MediaScoring.YearOf(name),
                Query = query,
            }));
        }
        // The search order is the relevance order; pages come back keyed by title.
        return items.OrderBy(i => i.Index).Select(i => i.Item).ToList();
    }
}

/// <summary>
/// Library of Congress (loc.gov JSON API, photos). Only items whose rights statement says public domain or "no known
/// restrictions on publication" (the LoC's wording for its public-domain government and press photos). loc.gov sits
/// behind a Cloudflare bot check that sometimes refuses API clients; the source then turns itself off for the run.
/// </summary>
public sealed class LibraryOfCongressSource(HttpClient http, MediaOptions options) : JsonMediaSource(http, options)
{
    public override string Name => "Library of Congress";

    protected override async Task<IReadOnlyList<MediaItem>> SearchCoreAsync(string query, MediaKind kind, int limit, CancellationToken ct)
    {
        if (kind != MediaKind.Image) return [];
        var uri = new Uri("https://www.loc.gov/photos/?" + Query(new Dictionary<string, string>
        {
            ["q"] = query,
            ["fo"] = "json",
            ["c"] = limit.ToString(),
            ["fa"] = "online-format:image",
        }));
        using var doc = await GetJsonAsync(uri, ct);
        return Parse(doc.RootElement, query);
    }

    internal static IReadOnlyList<MediaItem> Parse(JsonElement root, string query)
    {
        var items = new List<MediaItem>();
        if (!root.TryGetProperty("results", out var results)) return items;
        foreach (var result in results.EnumerateArray())
        {
            var rights = $"{Str(result, "rights_advisory")} {Str(result, "rights")} {Str(result, "rights_information")}".ToLowerInvariant();
            if (!rights.Contains("no known restrictions") && !rights.Contains("public domain")) continue;
            if (!result.TryGetProperty("image_url", out var urls) || urls.ValueKind != JsonValueKind.Array || urls.GetArrayLength() == 0) continue;
            // The list goes from small to large; "#h=..&w=.." fragments give the size.
            var largest = urls.EnumerateArray().Select(u => u.GetString() ?? "").Where(u => u.StartsWith("https://", StringComparison.Ordinal)).LastOrDefault();
            if (largest is null) continue;
            var (width, height) = FragmentSize(largest);
            var page = Str(result, "id");
            items.Add(new MediaItem
            {
                Id = "loc:" + page,
                Kind = MediaKind.Image,
                Source = "Library of Congress",
                Title = TextUtil.Truncate(Str(result, "title"), 160),
                Author = Str(result, "contributor") is { Length: > 0 } c ? TextUtil.Truncate(c, 160) : "Library of Congress",
                License = "Public domain",
                SourceUrl = page,
                DownloadUrl = largest.Split('#')[0],
                Width = width,
                Height = height,
                Year = MediaScoring.YearOf(Str(result, "date")),
                Query = query,
            });
        }
        return items;
    }

    private static (int Width, int Height) FragmentSize(string url)
    {
        var fragment = url.Contains('#') ? url[(url.IndexOf('#') + 1)..] : "";
        int width = 0, height = 0;
        foreach (var part in fragment.Split('&'))
        {
            var kv = part.Split('=');
            if (kv.Length != 2 || !int.TryParse(kv[1], out var value)) continue;
            if (kv[0] == "w") width = value;
            if (kv[0] == "h") height = value;
        }
        return (width, height);
    }
}

/// <summary>
/// Internet Archive footage from the US National Archives (FedFlix collection: Signal Corps and other US government
/// films, public domain as works of the US government). Only items the National Archives created, or that carry a
/// public domain or CC0 license, are used.
/// </summary>
public sealed class InternetArchiveSource(HttpClient http, MediaOptions options) : JsonMediaSource(http, options)
{
    public override string Name => "Internet Archive";

    protected override async Task<IReadOnlyList<MediaItem>> SearchCoreAsync(string query, MediaKind kind, int limit, CancellationToken ct)
    {
        if (kind != MediaKind.Video) return [];
        var terms = string.Join(" AND ", query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2 && !t.All(char.IsDigit)).Select(t => t.Replace("\"", "")));
        if (terms.Length == 0) return [];
        var search = new Uri("https://archive.org/advancedsearch.php?" + Query(new Dictionary<string, string>
        {
            ["q"] = $"({terms}) AND mediatype:movies AND collection:(FedFlix)",
            ["rows"] = limit.ToString(),
            ["output"] = "json",
        }) + "&fl[]=identifier&fl[]=title&fl[]=creator&fl[]=licenseurl&fl[]=date");
        using var doc = await GetJsonAsync(search, ct);
        var items = new List<MediaItem>();
        foreach (var hit in doc.RootElement.GetProperty("response").GetProperty("docs").EnumerateArray())
        {
            var identifier = Str(hit, "identifier");
            var creator = Str(hit, "creator");
            var license = Str(hit, "licenseurl");
            var fromArchives = creator.Contains("National Archives", StringComparison.OrdinalIgnoreCase);
            var openLicense = LicensePolicy.Classify(null, license) is "Public domain" or "CC0";
            if (identifier.Length == 0 || !fromArchives && !openLicense) continue;
            var item = await DescribeAsync(identifier, Str(hit, "title"), creator, fromArchives, query, ct);
            if (item is not null) items.Add(item);
            if (items.Count >= 4) break; // each costs a metadata request; a few candidates are plenty
        }
        return items;
    }

    /// <summary>Picks the item's MP4 (largest width up to 1920) from its file list.</summary>
    private async Task<MediaItem?> DescribeAsync(string identifier, string title, string creator, bool fromArchives, string query, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(new Uri($"https://archive.org/metadata/{Uri.EscapeDataString(identifier)}"), ct);
        return ParseMetadata(doc.RootElement, identifier, title, creator, fromArchives, query);
    }

    internal static MediaItem? ParseMetadata(JsonElement root, string identifier, string title, string creator, bool fromArchives, string query)
    {
        if (!root.TryGetProperty("files", out var files)) return null;
        var best = files.EnumerateArray()
            .Select(f => new
            {
                Name = Str(f, "name"),
                Format = Str(f, "format"),
                Width = int.TryParse(Str(f, "width"), out var w) ? w : 0,
                Height = int.TryParse(Str(f, "height"), out var h) ? h : 0,
                Length = double.TryParse(Str(f, "length"), System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : 0,
            })
            .Where(f => f.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && f.Width is >= 480 and <= 1920 && f.Length >= 10)
            .OrderByDescending(f => f.Width)
            .FirstOrDefault();
        if (best is null) return null;
        var description = root.TryGetProperty("metadata", out var meta) ? Str(meta, "description") : "";
        var catalog = System.Text.RegularExpressions.Regex.Match(description, @"https://catalog\.archives\.gov/id/\d+");
        return new MediaItem
        {
            Id = "archive:" + identifier,
            Kind = MediaKind.Video,
            Source = fromArchives ? "Internet Archive (US National Archives)" : "Internet Archive",
            Title = TextUtil.Truncate(title, 160),
            Author = creator.Length > 0 ? creator : "US National Archives",
            License = fromArchives ? "Public domain (US government work)" : "Public domain",
            SourceUrl = catalog.Success ? $"https://archive.org/details/{identifier} ({catalog.Value})" : $"https://archive.org/details/{identifier}",
            DownloadUrl = $"https://archive.org/download/{Uri.EscapeDataString(identifier)}/{Uri.EscapeDataString(best.Name)}",
            Width = best.Width,
            Height = best.Height,
            DurationSeconds = best.Length,
            Year = meta.ValueKind == JsonValueKind.Object ? MediaScoring.YearOf(Str(meta, "date")) : null,
            Query = query,
        };
    }
}
