using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MinutoUtil.Core.Publishing;

/// <summary>Where the owner's Telegram bot and chat come from. The token is never written to config or logs.</summary>
public sealed record TelegramOptions
{
    public string BotTokenEnvVar { get; init; } = "MINUTO_TELEGRAM_BOT_TOKEN";
    public string ChatIdEnvVar { get; init; } = "MINUTO_TELEGRAM_CHAT_ID";
}

/// <summary>An inline button under a message; <see cref="Data"/> comes back to the relay (relay/) when it is tapped.</summary>
public sealed record TelegramButton(string Text, string Data);

/// <summary>
/// Messages to the owner through Minuto Útil's own Telegram bot (not Flopômetro's). The bot token is part of
/// Telegram's URL, so errors are rebuilt from Telegram's "description" and never carry the request address.
/// </summary>
public sealed class TelegramNotifier(HttpClient http, string botToken, string chatId)
{
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
    public async Task SendVideoAsync(string videoPath, string caption, IReadOnlyList<TelegramButton> buttons, CancellationToken ct)
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
        using var response = await http.PostAsync(Url("sendVideo"), form, ct);
        await EnsureOkAsync(response, "sendVideo", ct);
    }

    /// <summary>Uploads a picture (Telegram bots take up to 10 MB) with a caption.</summary>
    public async Task SendPhotoAsync(string photoPath, string caption, CancellationToken ct)
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
        using var response = await http.PostAsync(Url("sendPhoto"), form, ct);
        await EnsureOkAsync(response, "sendPhoto", ct);
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
        throw new HttpRequestException($"Telegram {method} returned {(int)response.StatusCode}: {TextUtil.Truncate(description, 200)}");
    }
}
