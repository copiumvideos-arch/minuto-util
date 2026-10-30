using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MinutoUtil.Core.Publishing;

public sealed class ZernioException(string message) : Exception(message);

/// <summary>
/// A TikTok post made through Zernio: its post id, Zernio's status for the TikTok entry (published, or pending when
/// Zernio retries a passing TikTok error on its own) and the video's address on TikTok once it is up.
/// </summary>
public sealed record ZernioPost(string PostId, string Status, string? Url);

/// <summary>
/// Posts a video to a TikTok account through Zernio (zernio.com/api/v1), whose TikTok app is approved for direct posts,
/// so the video goes up public at once: no inbox draft and no notification to miss. Three calls: a presigned upload
/// address, a PUT of the file to it, then the post with publishNow. The account is found by its TikTok username among
/// the accounts connected to the Zernio key, so a key with the wrong accounts fails before anything is posted.
/// </summary>
public sealed class ZernioPoster(HttpClient http, string apiKey)
{
    internal const string Api = "https://zernio.com/api/v1/";

    public async Task<ZernioPost> PostAsync(string videoPath, string caption, string username, string idempotencyKey, CancellationToken ct)
    {
        var accountId = await AccountIdAsync(username, ct);
        var size = new FileInfo(videoPath).Length;

        using var presigned = await SendAsync(HttpMethod.Post, "media/presign",
            new JsonObject { ["filename"] = Path.GetFileName(videoPath), ["contentType"] = "video/mp4", ["size"] = size }, null, "upload address", ct);
        var root = presigned.RootElement;
        var uploadUrl = root.TryGetProperty("uploadUrl", out var u) ? u.GetString() : null;
        var publicUrl = root.TryGetProperty("publicUrl", out var p) ? p.GetString() : null;
        // The video goes wherever this URL points; only https is accepted.
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var upload) || upload.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(publicUrl))
            throw new ZernioException("Zernio returned no usable upload address.");

        await using (var file = File.OpenRead(videoPath))
        {
            // The upload address carries its own signature; the API key does not go to it.
            using var put = new HttpRequestMessage(HttpMethod.Put, upload) { Content = new StreamContent(file) };
            put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            put.Content.Headers.ContentLength = size;
            using var stored = await http.SendAsync(put, ct);
            if (!stored.IsSuccessStatusCode)
                throw new ZernioException($"Zernio storage refused the video ({(int)stored.StatusCode}).");
        }

        var body = new JsonObject
        {
            ["content"] = caption,
            ["mediaItems"] = new JsonArray(new JsonObject { ["url"] = publicUrl, ["type"] = "video" }),
            ["platforms"] = new JsonArray(new JsonObject { ["platform"] = "tiktok", ["accountId"] = accountId }),
            ["tiktokSettings"] = new JsonObject
            {
                ["privacy_level"] = "PUBLIC_TO_EVERYONE",
                ["allow_comment"] = true,
                ["allow_duet"] = true,
                ["allow_stitch"] = true,
                // The owner reviewed this video on Telegram and tapped Publicar: that is the preview and the consent.
                ["content_preview_confirmed"] = true,
                ["express_consent_given"] = true,
            },
            ["publishNow"] = true,
        };
        using var created = await SendAsync(HttpMethod.Post, "posts", body, idempotencyKey, "post", ct, allowPartial: true);
        return ReadPost(created.RootElement);
    }

    /// <summary>
    /// The post from Zernio's answer. A 207 (allowed through by <see cref="SendAsync"/>) is a success only when Zernio
    /// kept the TikTok entry to retry it itself (post status "scheduled"); "failed" or "partial" is an error.
    /// </summary>
    internal static ZernioPost ReadPost(JsonElement root)
    {
        if (!root.TryGetProperty("post", out var post) || !post.TryGetProperty("_id", out var id) || id.GetString() is not { Length: > 0 } postId)
            throw new ZernioException("Zernio answered without the post.");
        var postStatus = post.TryGetProperty("status", out var ps) ? ps.GetString() ?? "" : "";
        var tiktok = post.TryGetProperty("platforms", out var platforms) && platforms.ValueKind == JsonValueKind.Array
            ? platforms.EnumerateArray().FirstOrDefault(e => e.TryGetProperty("platform", out var name) && name.GetString() == "tiktok")
            : default;
        string? Text(string name) => tiktok.ValueKind == JsonValueKind.Object && tiktok.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var status = Text("status") ?? postStatus;
        if (postStatus is "failed" or "partial" || status == "failed")
        {
            var reason = Text("errorMessage") ?? (root.TryGetProperty("error", out var e) ? e.GetString() : null) ?? "no reason given";
            throw new ZernioException($"TikTok refused the post through Zernio: {TextUtil.Truncate(reason, 300)} (post {postId}).");
        }
        return new ZernioPost(postId, status, Text("platformPostUrl"));
    }

    /// <summary>The Zernio id of the connected TikTok account with this username (with or without the @).</summary>
    private async Task<string> AccountIdAsync(string username, CancellationToken ct)
    {
        using var list = await SendAsync(HttpMethod.Get, "accounts?platform=tiktok", null, null, "account list", ct);
        var wanted = username.TrimStart('@');
        var accounts = list.RootElement.TryGetProperty("accounts", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : [];
        string Name(JsonElement account) => account.TryGetProperty("username", out var n) ? n.GetString() ?? "" : "";
        var match = accounts.FirstOrDefault(x => string.Equals(Name(x).TrimStart('@'), wanted, StringComparison.OrdinalIgnoreCase));
        // The reference calls the id "_id", the quickstart "id"; either is accepted.
        var id = match.ValueKind == JsonValueKind.Object && (match.TryGetProperty("_id", out var i) || match.TryGetProperty("id", out i)) ? i.GetString() : null;
        if (id is not { Length: > 0 } accountId)
            throw new ZernioException($"No TikTok account @{wanted} is connected to this Zernio key (connected: " +
                                      $"{(accounts.Count == 0 ? "none" : string.Join(", ", accounts.Select(x => "@" + Name(x).TrimStart('@'))))}). Connect it in the Zernio dashboard.");
        return accountId;
    }

    /// <summary>Calls the API with the key; anything but a 2xx (or a 207 when <paramref name="allowPartial"/>) is an error with Zernio's message.</summary>
    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, JsonObject? json, string? idempotencyKey, string step,
        CancellationToken ct, bool allowPartial = false)
    {
        using var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (json is not null) request.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var text = await SafeHttp.ReadLimitedAsync(response, 2_000_000, ct);
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
        }
        if (response.IsSuccessStatusCode && (allowPartial || response.StatusCode != HttpStatusCode.MultiStatus) && doc is not null) return doc;

        var error = doc?.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var code = doc?.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        doc?.Dispose();
        var hint = response.StatusCode == HttpStatusCode.Unauthorized ? " Check ZERNIO_API_KEY." : "";
        throw new ZernioException($"Zernio {step} failed ({(int)response.StatusCode}{(code is null ? "" : $" {code}")}): {TextUtil.Truncate(error ?? text, 300)}{hint}");
    }
}
