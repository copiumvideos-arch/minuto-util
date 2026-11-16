using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MinutoUtil.Core.Publishing;

/// <summary>
/// Where the owner's Telegram bot and chat come from: &lt;prefix&gt;_TELEGRAM_BOT_TOKEN and &lt;prefix&gt;_TELEGRAM_CHAT_ID,
/// filled by <see cref="Channel.Load"/>. Each channel has its own bot. The token is never written to config or logs.
/// </summary>
public sealed record TelegramOptions
{
    public string BotTokenEnvVar { get; init; } = "";
    public string ChatIdEnvVar { get; init; } = "";
}

/// <summary>An inline button under a message; <see cref="Data"/> comes back to the relay (relay/) when it is tapped.</summary>
public sealed record TelegramButton(string Text, string Data);

/// <summary>
/// Messages to the owner through the channel's own Telegram bot (not Flopômetro's). The bot token is part of
/// Telegram's URL, so errors are rebuilt from Telegram's "description" and never carry the request address.
/// Videos and pictures go through <paramref name="uploads"/> when given: a 40 MB video from a GitHub runner can take
/// longer than the 30 seconds a message gets (on 2026-11-14 a finished video was lost that way).
/// </summary>
public sealed class TelegramNotifier(HttpClient http, string botToken, string chatId, HttpClient? uploads = null)
{
    /// <summary>How often an upload is tried when Telegram does not answer in time or answers 5xx.</summary>
    public const int UploadAttempts = 2;

    /// <summary>Telegram's limit for a video caption; longer text is cut.</summary>
    public const int MaxCaption = 1024;

    public Task SendTextAsync(string text, CancellationToken ct) => SendTextAsync(text, [], ct);

    public async Task SendTextAsync(string text, IReadOnlyList<TelegramButton> buttons, CancellationToken ct, int perRow = 5)
    {
        var body = new Dictionary<string, object> { ["chat_id"] = chatId, ["text"] = text, ["link_preview_options"] = new { is_disabled = true } };
        if (buttons.Count > 0) body["reply_markup"] = Keyboard(buttons, perRow);
        using var response = await http.PostAsJsonAsync(Url("sendMessage"), body, ct);
        await EnsureOkAsync(response, "sendMessage", ct);
    }

    /// <summary>Uploads a video (Telegram bots take up to 50 MB) with a caption and one row of buttons.</summary>
    public Task SendVideoAsync(string videoPath, string caption, IReadOnlyList<TelegramButton> buttons, CancellationToken ct) =>
        UploadAsync("sendVideo", () => SendVideoOnceAsync(videoPath, caption, buttons, ct), ct);

    private async Task SendVideoOnceAsync(string videoPath, string caption, IReadOnlyList<TelegramButton> buttons, CancellationToken ct)
    {
        await using var file = File.OpenRead(videoPath);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(chatId), "chat_id" },
            { new StringContent(TextUtil.Truncate(caption, MaxCaption)), "caption" },
            { new StringContent("true"), "supports_streaming" },
            { new StringContent(JsonSerializer.Serialize(Keyboard(buttons, 5))), "reply_markup" },
        };
        var video = new StreamContent(file);
        video.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(video, "video", Path.GetFileName(videoPath));
        using var response = await (uploads ?? http).PostAsync(Url("sendVideo"), form, ct);
        await EnsureOkAsync(response, "sendVideo", ct);
    }

    /// <summary>Uploads a picture (Telegram bots take up to 10 MB) with a caption.</summary>
    public Task SendPhotoAsync(string photoPath, string caption, CancellationToken ct) =>
        UploadAsync("sendPhoto", () => SendPhotoOnceAsync(photoPath, caption, ct), ct);

    private async Task SendPhotoOnceAsync(string photoPath, string caption, CancellationToken ct)
    {
        await using var file = File.OpenRead(photoPath);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(chatId), "chat_id" },
            { new StringContent(TextUtil.Truncate(caption, MaxCaption)), "caption" },
        };
        var photo = new StreamContent(file);
        photo.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(photo, "photo", Path.GetFileName(photoPath));
        using var response = await (uploads ?? http).PostAsync(Url("sendPhoto"), form, ct);
        await EnsureOkAsync(response, "sendPhoto", ct);
    }

    /// <summary>
    /// One upload, tried again once after a timeout or a 5xx. A timed-out upload may still have arrived, so the owner
    /// can get the same video twice; that beats a finished video lost.
    /// </summary>
    private static async Task UploadAsync(string method, Func<Task> send, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await send();
                return;
            }
            catch (Exception ex) when (attempt < UploadAttempts && !ct.IsCancellationRequested
                                       && (ex is TaskCanceledException || ex is TelegramServerException))
            {
                Log.Warn($"Telegram {method} failed ({TextUtil.Truncate(ex.Message, 120)}); trying once more");
            }
        }
    }

    private string Url(string method) => $"https://api.telegram.org/bot{botToken}/{method}";

    /// <summary>Rows of <paramref name="perRow"/> buttons (the daily idea list's 1 to 10 in two rows, the queue's ⏫ and 🗑 in pairs).</summary>
    private static object Keyboard(IReadOnlyList<TelegramButton> buttons, int perRow) =>
        new { inline_keyboard = buttons.Chunk(perRow).Select(row => row.Select(b => new { text = b.Text, callback_data = b.Data }).ToArray()).ToArray() };

    private static async Task EnsureOkAsync(HttpResponseMessage response, string method, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        string description;
        try
        {
            using var doc = JsonDocument.Parse(body);
            description = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            description = "";
        }
        var message = $"Telegram {method} returned {(int)response.StatusCode}: {TextUtil.Truncate(description, 200)}";
        throw (int)response.StatusCode >= 500 ? new TelegramServerException(message) : new HttpRequestException(message);
    }
}

/// <summary>Telegram answered 5xx: worth one more try.</summary>
public sealed class TelegramServerException(string message) : HttpRequestException(message);
