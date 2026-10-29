using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinutoUtil.Core.Video;

/// <summary>A painted picture for the thumbnail and the file extension its bytes call for (".jpg", ".png", ".webp").</summary>
public sealed record CoverArt(byte[] Bytes, string Extension, string Model);

/// <summary>Paints the picture behind the thumbnail text from a scene description.</summary>
public interface ICoverPainter
{
    Task<CoverArt> PaintAsync(string scene, CancellationToken ct);
}

public sealed class CoverArtException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Cloudflare Workers AI's image models on the owner's free account (the same token and account as the spare writer).
/// One request per call and never a retry: a failure leaves the thumbnail on the archive picture. The token goes in
/// the Authorization header, never the URL or a log. The answer is untrusted data: it must decode to a JPEG, PNG or
/// WebP of a sane size, and it is only ever read by FFmpeg.
/// </summary>
public sealed partial class CloudflareCoverPainter : ICoverPainter
{
    /// <summary>A 864x1536 JPEG is about 300 KB; anything near this is not a cover.</summary>
    internal const int MaxBytes = 12 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly CloudflareAiOptions _options;
    private readonly RunUsage? _usage;
    private readonly Uri _endpoint;

    public CloudflareCoverPainter(HttpClient http, string token, string accountId, CloudflareAiOptions options, RunUsage? usage = null)
    {
        if (!AccountId().IsMatch(accountId)) throw new CoverArtException("The Cloudflare account id must be 32 hex characters.");
        if (!ModelId().IsMatch(options.ImageModel)) throw new CoverArtException($"\"{options.ImageModel}\" is not a Workers AI model id.");
        (_http, _token, _options, _usage) = (http, token, options, usage);
        _endpoint = new Uri($"https://api.cloudflare.com/client/v4/accounts/{accountId}/ai/run/{options.ImageModel}");
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex AccountId();

    [GeneratedRegex(@"^@cf/[a-z0-9-]+/[a-z0-9.-]+$")]
    private static partial Regex ModelId();

    /// <summary>The whole prompt: the channel's fixed style, then the story's scene.</summary>
    public static string Prompt(CloudflareAiOptions options, string scene) => $"{options.ImageStyle.Trim()} Scene: {scene.Trim()}";

    public async Task<CoverArt> PaintAsync(string scene, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scene)) throw new CoverArtException("No scene to paint.");
        // The FLUX.2 models take multipart form data even for a prompt alone. The names are quoted by hand: .NET leaves
        // them bare, which not every form parser accepts.
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Prompt(_options, scene)), "\"prompt\"" },
            { new StringContent(_options.ImageWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)), "\"width\"" },
            { new StringContent(_options.ImageHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)), "\"height\"" },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        _usage?.AddCoverImage();
        string body;
        try
        {
            using var response = await _http.SendAsync(request, ct);
            body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new CoverArtException($"cloudflare:{_options.ImageModel} returned {(int)response.StatusCode}: {TextUtil.Truncate(body, 300)}");
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new CoverArtException($"cloudflare:{_options.ImageModel} failed: {ex.Message}", ex);
        }

        var bytes = Decode(body) ?? throw new CoverArtException($"cloudflare:{_options.ImageModel} answered without an image: {TextUtil.Truncate(body, 200)}");
        return new CoverArt(bytes, ExtensionOf(bytes) ?? throw new CoverArtException("The painted cover is not a JPEG, PNG or WebP image."), _options.ImageModel);
    }

    /// <summary>The image bytes from {"result":{"image":"base64"}} (REST) or {"image":"base64"}, or null.</summary>
    internal static byte[]? Decode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object) root = result;
            if (!root.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.String) return null;
            var text = image.GetString()!;
            // Some models answer with a data URI.
            if (text.StartsWith("data:", StringComparison.Ordinal) && text.IndexOf(',') is var comma and > 0) text = text[(comma + 1)..];
            if (text.Length > MaxBytes / 3 * 4 + 4) throw new CoverArtException("The painted cover is far too large.");
            return Convert.FromBase64String(text);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>".jpg", ".png" or ".webp" from the file's first bytes, or null for anything else.</summary>
    internal static string? ExtensionOf(byte[] bytes)
    {
        if (bytes.Length < 1024) return null;
        if (bytes is [0xFF, 0xD8, 0xFF, ..]) return ".jpg";
        if (bytes is [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..]) return ".png";
        if (bytes is [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..]) return ".webp";
        return null;
    }
}

/// <summary>A painted picture in a run folder and the model that painted it.</summary>
public sealed record PaintedCover(string Path, string Model);

/// <summary>
/// The painted picture of a run folder: cover-art.* with cover-art.json (the prompt and model), reused while the prompt
/// is the same, so a re-render or a second cover command makes no request; a changed scene paints again.
/// </summary>
public static class CoverArtFile
{
    private sealed record Record(string File, string Prompt, string Model);

    /// <summary>The saved or newly painted picture, or null with a warning when the painting fails (one request, no retry).</summary>
    public static async Task<PaintedCover?> PaintOrReuseAsync(string folder, string scene, CloudflareAiOptions options, ICoverPainter painter,
        CancellationToken ct)
    {
        var prompt = CloudflareCoverPainter.Prompt(options, scene);
        var recordPath = Path.Combine(folder, "cover-art.json");
        if (File.Exists(recordPath)
            && JsonSerializer.Deserialize<Record>(await File.ReadAllTextAsync(recordPath, ct), Json.Options) is { } saved
            && saved.Prompt == prompt && Path.GetFileName(saved.File) == saved.File && File.Exists(Path.Combine(folder, saved.File)))
        {
            Log.Info($"thumbnail: reused {saved.File} (no image request)");
            return new PaintedCover(Path.Combine(folder, saved.File), saved.Model);
        }

        try
        {
            var art = await painter.PaintAsync(scene, ct);
            var file = "cover-art" + art.Extension;
            foreach (var old in new[] { ".jpg", ".png", ".webp" }.Select(e => Path.Combine(folder, "cover-art" + e)).Where(File.Exists))
                File.Delete(old);
            await File.WriteAllBytesAsync(Path.Combine(folder, file), art.Bytes, ct);
            await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(new Record(file, prompt, art.Model), Json.Options), ct);
            Log.Info($"thumbnail: painted {file} with {art.Model}, {art.Bytes.Length / 1024} KB");
            return new PaintedCover(Path.Combine(folder, file), art.Model);
        }
        catch (CoverArtException ex)
        {
            Log.Warn($"thumbnail: no painted cover ({ex.Message})");
            return null;
        }
    }
}
