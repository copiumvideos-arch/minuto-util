using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MinutoUtil.Core.Publishing;

public sealed class TikTokUploadException(string message) : Exception(message);

/// <summary>How the file is cut for TikTok: every chunk but the last is <see cref="ChunkSize"/>; the last takes the remainder.</summary>
public sealed record ChunkPlan(long VideoSize, long ChunkSize, int TotalChunkCount)
{
    /// <summary>
    /// TikTok's rules: chunks of 5 to 64 MB, the last one up to 128 MB, at most 1000 chunks, a file under 5 MB in one
    /// piece. The docs do not say whether MB means 10^6 or 2^20 bytes, so the sizes here fit both readings: a file up to
    /// 64,000,000 bytes goes whole, a larger one in 20,000,000-byte chunks (the last between 20 and 40 MB).
    /// </summary>
    public const long SingleChunkMax = 64_000_000;
    public const long MultiChunkSize = 20_000_000;

    public static ChunkPlan For(long videoSize)
    {
        if (videoSize <= 0) throw new InvalidDataException("The video file is empty.");
        if (videoSize <= SingleChunkMax) return new ChunkPlan(videoSize, videoSize, 1);
        // Rounded down: the remainder rides on the last chunk, as TikTok's total_chunk_count rule expects.
        var count = videoSize / MultiChunkSize;
        if (count > 1000) throw new InvalidDataException($"The video is too large for TikTok's 1000-chunk limit ({videoSize} bytes).");
        return new ChunkPlan(videoSize, MultiChunkSize, (int)count);
    }

    /// <summary>First and last byte (inclusive) of chunk <paramref name="index"/>.</summary>
    public (long First, long Last) Range(int index) =>
        (index * ChunkSize, index == TotalChunkCount - 1 ? VideoSize - 1 : (index + 1) * ChunkSize - 1);
}

/// <summary>What TikTok said about an upload: SEND_TO_USER_INBOX means the draft is waiting in the owner's inbox.</summary>
public sealed record TikTokStatus(string Status, string? FailReason, long? UploadedBytes);

public sealed record TikTokUpload(string PublishId, TikTokStatus Status);

/// <summary>
/// TikTok Content Posting API, upload to inbox (scope video.upload): init with FILE_UPLOAD, PUT the chunks in order to
/// the upload_url, then poll the status until TikTok has sent the draft to the owner's inbox. The owner finishes the
/// post (caption, cover, sound, visibility) in the TikTok app.
/// </summary>
public sealed class TikTokUploader(HttpClient http, Func<CancellationToken, Task<string>> accessToken, TimeSpan? pollInterval = null, TimeSpan? retryDelay = null)
{
    internal const string InitEndpoint = "https://open.tiktokapis.com/v2/post/publish/inbox/video/init/";
    internal const string StatusEndpoint = "https://open.tiktokapis.com/v2/post/publish/status/fetch/";

    private const int MaxChunkRetries = 3;

    /// <summary>The status endpoint allows 30 requests a minute per token; every 5 seconds stays well under.</summary>
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(3);

    public async Task<TikTokUpload> UploadAsync(string videoPath, TimeSpan statusWait, CancellationToken ct)
    {
        var plan = ChunkPlan.For(new FileInfo(videoPath).Length);
        var (publishId, uploadUrl) = await InitAsync(plan, ct);
        Log.Info($"TikTok upload started: publish_id {publishId}, {plan.TotalChunkCount} chunk(s)");
        await SendChunksAsync(videoPath, plan, uploadUrl, ct);
        return new TikTokUpload(publishId, await WaitForInboxAsync(publishId, statusWait, ct));
    }

    internal static string InitBody(ChunkPlan plan) => new JsonObject
    {
        ["source_info"] = new JsonObject
        {
            ["source"] = "FILE_UPLOAD",
            ["video_size"] = plan.VideoSize,
            ["chunk_size"] = plan.ChunkSize,
            ["total_chunk_count"] = plan.TotalChunkCount,
        },
    }.ToJsonString();

    private async Task<(string PublishId, Uri UploadUrl)> InitAsync(ChunkPlan plan, CancellationToken ct)
    {
        using var data = await PostJsonAsync(InitEndpoint, InitBody(plan), "starting the upload", ct);
        var publishId = data.RootElement.GetProperty("publish_id").GetString();
        var uploadUrl = data.RootElement.TryGetProperty("upload_url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(publishId) || !Uri.TryCreate(uploadUrl, UriKind.Absolute, out var url))
            throw new TikTokUploadException("TikTok returned no publish_id or upload_url.");
        // The video goes wherever this URL points; only TikTok's own API hosts over https are accepted.
        if (url.Scheme != Uri.UriSchemeHttps || !url.Host.EndsWith(".tiktokapis.com", StringComparison.OrdinalIgnoreCase))
            throw new TikTokUploadException($"Unexpected TikTok upload host '{url.Host}'.");
        return (publishId, url);
    }

    /// <summary>Chunks go up in order; TikTok answers 206 for each but the last and 201 when it has the whole file.</summary>
    private async Task SendChunksAsync(string videoPath, ChunkPlan plan, Uri uploadUrl, CancellationToken ct)
    {
        await using var file = File.OpenRead(videoPath);
        for (var index = 0; index < plan.TotalChunkCount; index++)
        {
            var (first, last) = plan.Range(index);
            var buffer = new byte[last - first + 1];
            file.Position = first;
            await file.ReadExactlyAsync(buffer, ct);
            var expected = index == plan.TotalChunkCount - 1 ? HttpStatusCode.Created : HttpStatusCode.PartialContent;

            for (var attempt = 1; ; attempt++)
            {
                // The upload URL carries its own authorization; no bearer token goes to it.
                using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(buffer) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                request.Content.Headers.ContentRange = new ContentRangeHeaderValue(first, last, plan.VideoSize);
                HttpStatusCode? status = null;
                string error;
                try
                {
                    using var response = await http.SendAsync(request, ct);
                    status = response.StatusCode;
                    // A 201 before the last chunk would mean TikTok counted differently; it is accepted as done.
                    if (response.StatusCode == expected || response.StatusCode == HttpStatusCode.Created) break;
                    error = $"{(int)response.StatusCode} {TextUtil.Truncate(await SafeHttp.ReadLimitedAsync(response, 64_000, ct), 200)}";
                }
                catch (HttpRequestException ex)
                {
                    error = ex.Message;
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    error = "timed out";
                }

                var retryable = status is null || (int)status >= 500;
                if (!retryable || attempt > MaxChunkRetries)
                    throw new TikTokUploadException($"TikTok chunk {index + 1}/{plan.TotalChunkCount} failed: {error}" +
                        (status == HttpStatusCode.Forbidden ? " (the upload URL expires after an hour)" : ""));
                Log.Warn($"TikTok chunk {index + 1}/{plan.TotalChunkCount} failed ({error}), sending it again");
                await Task.Delay(_retryDelay * attempt, ct);
            }
        }
    }

    /// <summary>
    /// Polls until the draft is in the inbox (or posted, or failed). Still processing after <paramref name="wait"/> is
    /// not an error: TikTok finishes on its own and the inbox notification arrives later.
    /// </summary>
    internal async Task<TikTokStatus> WaitForInboxAsync(string publishId, TimeSpan wait, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            var status = await FetchStatusAsync(publishId, ct);
            switch (status.Status)
            {
                case "SEND_TO_USER_INBOX" or "PUBLISH_COMPLETE":
                    return status;
                case "FAILED":
                    throw new TikTokUploadException($"TikTok could not process the video: {status.FailReason ?? "no reason given"} (publish_id {publishId}).");
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Log.Warn($"TikTok is still at {status.Status} after {wait.TotalSeconds:0} s; the draft should reach the inbox later");
                return status;
            }
            await Task.Delay(_pollInterval, ct);
        }
    }

    /// <summary>TikTok's current status of an upload; a draft stays SEND_TO_USER_INBOX until the owner posts it.</summary>
    public async Task<TikTokStatus> FetchStatusAsync(string publishId, CancellationToken ct)
    {
        using var data = await PostJsonAsync(StatusEndpoint, new JsonObject { ["publish_id"] = publishId }.ToJsonString(), "status check", ct);
        var root = data.RootElement;
        return new TikTokStatus(
            root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
            root.TryGetProperty("fail_reason", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } reason ? reason : null,
            root.TryGetProperty("uploaded_bytes", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : null);
    }

    /// <summary>POSTs JSON with the bearer token and returns the "data" object; anything but error.code "ok" is an error.</summary>
    private async Task<JsonDocument> PostJsonAsync(string endpoint, string json, string step, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Content.Headers.ContentType!.CharSet = "UTF-8";
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(ct));
        using var response = await http.SendAsync(request, ct);
        var body = await SafeHttp.ReadLimitedAsync(response, 1_000_000, ct);

        string code = "", message = "";
        JsonElement data = default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                code = error.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            }
            if (doc.RootElement.TryGetProperty("data", out var d)) data = d.Clone();
        }
        catch (JsonException)
        {
        }
        if (response.IsSuccessStatusCode && code == "ok" && data.ValueKind == JsonValueKind.Object)
            return JsonDocument.Parse(data.GetRawText());

        var hint = code switch
        {
            "access_token_invalid" => $" Run '{TikTokAuth.LoginCommand}' again.",
            "scope_not_authorized" => " The sign-in lacks video.upload; check the app's scopes and sign in again.",
            "spam_risk_too_many_pending_share" => " TikTok allows 5 drafts waiting in the inbox per 24 hours; finish or delete some in the app.",
            "rate_limit_exceeded" => " Too many requests; wait a minute.",
            _ => "",
        };
        throw new TikTokUploadException($"TikTok {step} failed ({(int)response.StatusCode} {code}): {TextUtil.Truncate(message, 200)}{hint}");
    }
}
