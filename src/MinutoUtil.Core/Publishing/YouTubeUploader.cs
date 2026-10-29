using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MinutoUtil.Core.Publishing;

public sealed class YouTubeUploadException(string message, bool retryLater = false) : Exception(message)
{
    /// <summary>True for daily quota and upload limits: a later run can try again, nothing is wrong with the video.</summary>
    public bool RetryLater { get; } = retryLater;
}

public sealed record VideoDetails(
    string Title,
    string Description,
    IReadOnlyList<string> Tags,
    string CategoryId,
    string PrivacyStatus,
    bool MadeForKids,
    bool ContainsSyntheticMedia,
    string Language,
    string AudioLanguage);

public sealed record UploadedVideo(string VideoId, string PrivacyStatus)
{
    public string Url => $"https://youtube.com/shorts/{VideoId}";
}

/// <summary>
/// YouTube Data API v3 resumable upload (copied from Flopômetro and adapted): one request creates the upload session
/// with the metadata, then the file goes up in chunks. A dropped connection or a 5xx asks YouTube how much it already
/// has and continues from there.
/// </summary>
public sealed class YouTubeUploader(HttpClient http, Func<CancellationToken, Task<string>> accessToken, TimeSpan? retryDelay = null)
{
    internal const string UploadEndpoint = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";
    internal const string ChannelsEndpoint = "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true";
    internal const string ThumbnailEndpoint = "https://www.googleapis.com/upload/youtube/v3/thumbnails/set?uploadType=media&videoId=";

    /// <summary>Chunks must be a multiple of 256 KiB.</summary>
    internal const int ChunkSize = 32 * 256 * 1024;

    private const int MaxRetries = 6;
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(3);

    /// <summary>
    /// The channel the token belongs to (channels?mine=true, 1 quota unit). Refuses anything but
    /// <paramref name="expectedChannelId"/>, so a sign-in with the Flopômetro account can never upload here.
    /// </summary>
    public async Task<string> EnsureChannelAsync(string expectedChannelId, CancellationToken ct) =>
        await EnsureChannelAsync(http, await accessToken(ct), expectedChannelId, ct);

    public static async Task<string> EnsureChannelAsync(HttpClient http, string token, string expectedChannelId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ChannelsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        var body = await SafeHttp.ReadLimitedAsync(response, 1_000_000, ct);
        if (!response.IsSuccessStatusCode)
            throw new YouTubeUploadException($"YouTube channel lookup failed ({(int)response.StatusCode}): {TextUtil.Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        var channels = doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(i => (Id: i.GetProperty("id").GetString() ?? "",
                Title: i.TryGetProperty("snippet", out var s) && s.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "")).ToList()
            : [];
        if (channels.Count == 0)
            throw new YouTubeUploadException("The signed-in Google account has no YouTube channel. Sign in with the channel's own YouTube channel (pick it in the account chooser).");
        if (channels.FirstOrDefault(c => c.Id == expectedChannelId) is { Id.Length: > 0 } match) return match.Title;
        throw new YouTubeUploadException(
            $"The sign-in belongs to channel {channels[0].Id} ({channels[0].Title}), not {expectedChannelId}. " +
            "Nothing was saved or uploaded. Sign in again and pick the YouTube channel named by youtube.channelId in the channel's config.json.");
    }

    public async Task<UploadedVideo> UploadAsync(string videoPath, VideoDetails details, CancellationToken ct)
    {
        var total = new FileInfo(videoPath).Length;
        var session = await StartSessionAsync(details, total, ct);

        await using var file = File.OpenRead(videoPath);
        var buffer = new byte[ChunkSize];
        long offset = 0;
        var failures = 0;
        while (true)
        {
            HttpResponseMessage? response = null;
            try
            {
                file.Position = offset;
                var want = (int)Math.Min(ChunkSize, total - offset);
                var length = await file.ReadAtLeastAsync(buffer.AsMemory(0, want), want, true, ct);
                using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(buffer, 0, length) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
                request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, total);
                response = await http.SendAsync(request, ct);

                if (response.IsSuccessStatusCode) return await ParseUploadedAsync(response, ct);
                if ((int)response.StatusCode == 308)
                {
                    offset = NextOffset(response);
                    failures = 0;
                    continue;
                }
                if (!IsRetryable(response.StatusCode))
                    throw await ErrorAsync("upload", response, ct);
                Log.Warn($"YouTube upload chunk returned {(int)response.StatusCode}, resuming");
            }
            catch (HttpRequestException ex)
            {
                Log.Warn($"YouTube upload interrupted ({ex.Message}), resuming");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Warn("YouTube upload chunk timed out, resuming");
            }
            finally
            {
                response?.Dispose();
            }

            if (++failures > MaxRetries) throw new YouTubeUploadException($"YouTube upload kept failing after {MaxRetries} retries.", retryLater: true);
            await Task.Delay(_retryDelay * Math.Pow(2, failures - 1), ct);
            var status = await QueryAsync(session, total, ct);
            if (status.Done is not null) return status.Done;
            offset = status.Offset;
        }
    }

    /// <summary>
    /// Sets a custom thumbnail (thumbnails.set, 50 quota units; the youtube.upload scope allows it). YouTube accepts it
    /// only from a channel that has verified a phone number, and takes JPEG or PNG up to 2 MB.
    /// </summary>
    public async Task SetThumbnailAsync(string videoId, string imagePath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ThumbnailEndpoint + Uri.EscapeDataString(videoId))
        {
            Content = new ByteArrayContent(await File.ReadAllBytesAsync(imagePath, ct)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
            Path.GetExtension(imagePath).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync("setting the thumbnail", response, ct);
    }

    private async Task<Uri> StartSessionAsync(VideoDetails details, long total, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint)
        {
            Content = new StringContent(MetadataJson(details), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
        request.Headers.Add("X-Upload-Content-Type", "video/mp4");
        request.Headers.Add("X-Upload-Content-Length", total.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync("starting the upload", response, ct);
        var session = response.Headers.Location
            ?? throw new YouTubeUploadException("YouTube did not return an upload session URL.");
        // The session URL is where the whole video (and the access token) goes next; only Google's API hosts are accepted.
        if (session.Scheme != Uri.UriSchemeHttps || !session.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase))
            throw new YouTubeUploadException($"Unexpected upload session host '{session.Host}'.");
        return session;
    }

    /// <summary>Asks how many bytes YouTube already has (an empty PUT with "bytes */total").</summary>
    private async Task<(long Offset, UploadedVideo? Done)> QueryAsync(Uri session, long total, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent([]) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
        request.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes */{total}");
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return (total, await ParseUploadedAsync(response, ct));
            if ((int)response.StatusCode == 308) return (NextOffset(response), null);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new YouTubeUploadException("The YouTube upload session expired. Publish the folder again.", retryLater: true);
            return (0, null);
        }
        catch (HttpRequestException)
        {
            return (0, null);
        }
    }

    /// <summary>The Range header ("bytes=0-1048575") says what YouTube has; no header means nothing yet.</summary>
    internal static long NextOffset(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Range", out var values)) return 0;
        var range = values.First();
        var dash = range.LastIndexOf('-');
        return dash > 0 && long.TryParse(range[(dash + 1)..], out var last) ? last + 1 : 0;
    }

    internal static string MetadataJson(VideoDetails details) => new JsonObject
    {
        ["snippet"] = new JsonObject
        {
            ["title"] = details.Title,
            ["description"] = details.Description,
            ["tags"] = new JsonArray(details.Tags.Select(t => (JsonNode)t).ToArray()),
            ["categoryId"] = details.CategoryId,
            ["defaultLanguage"] = details.Language,
            ["defaultAudioLanguage"] = details.AudioLanguage,
        },
        ["status"] = new JsonObject
        {
            ["privacyStatus"] = details.PrivacyStatus,
            ["selfDeclaredMadeForKids"] = details.MadeForKids,
            ["containsSyntheticMedia"] = details.ContainsSyntheticMedia,
            ["embeddable"] = true,
            ["license"] = "youtube",
        },
    }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static async Task<UploadedVideo> ParseUploadedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await SafeHttp.ReadLimitedAsync(response, 1_000_000, ct));
        var id = doc.RootElement.GetProperty("id").GetString() ?? throw new YouTubeUploadException("YouTube returned no video id.");
        var privacy = doc.RootElement.TryGetProperty("status", out var status) && status.TryGetProperty("privacyStatus", out var p)
            ? p.GetString() ?? "unknown"
            : "unknown";
        return new UploadedVideo(id, privacy);
    }

    private static bool IsRetryable(HttpStatusCode code) =>
        code is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout;

    private static async Task<YouTubeUploadException> ErrorAsync(string step, HttpResponseMessage response, CancellationToken ct)
    {
        var body = await SafeHttp.ReadLimitedAsync(response, 1_000_000, ct);
        var reason = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            reason = doc.RootElement.GetProperty("error").GetProperty("errors")[0].GetProperty("reason").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
        }
        var limit = reason is "quotaExceeded" or "uploadLimitExceeded" or "rateLimitExceeded" or "userRateLimitExceeded";
        var hint = reason switch
        {
            "quotaExceeded" => " The daily API quota is used up; it resets at midnight Pacific time.",
            "uploadLimitExceeded" => " The channel hit YouTube's daily upload limit.",
            "forbidden" => " Custom thumbnails need a channel with a verified phone number (youtube.com/verify).",
            "mediaBodyRequired" or "invalidImage" => " YouTube did not accept the image (JPEG or PNG up to 2 MB).",
            "youtubeSignupRequired" => " The signed-in Google account has no YouTube channel. Sign in with the channel's account.",
            _ => "",
        };
        return new YouTubeUploadException(
            $"YouTube {step} failed ({(int)response.StatusCode} {reason}).{hint} {TextUtil.Truncate(body, 300)}", retryLater: limit);
    }
}
