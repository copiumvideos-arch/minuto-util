using System.Net.Http.Json;
using System.Text.Json;

namespace MinutoUtil.Core.Publishing;

/// <summary>Where the owner's Telegram bot and chat come from. The token is never written to config or logs.</summary>
public sealed record TelegramOptions
{
    public string BotTokenEnvVar { get; init; } = "MINUTO_TELEGRAM_BOT_TOKEN";
    public string ChatIdEnvVar { get; init; } = "MINUTO_TELEGRAM_CHAT_ID";
}

/// <summary>
/// Plain text messages to the owner through Minuto Útil's own Telegram bot (not Flopômetro's). The bot token is part
/// of Telegram's URL, so errors are rebuilt from Telegram's "description" and never carry the request address.
/// </summary>
public sealed class TelegramNotifier(HttpClient http, string botToken, string chatId)
{
    public async Task SendTextAsync(string text, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{botToken}/sendMessage",
            new Dictionary<string, string> { ["chat_id"] = chatId, ["text"] = text }, ct);
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
        throw new HttpRequestException($"Telegram sendMessage returned {(int)response.StatusCode}: {TextUtil.Truncate(description, 200)}");
    }
}
