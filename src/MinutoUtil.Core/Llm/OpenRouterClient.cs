using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MinutoUtil.Core.Llm;

/// <summary>
/// OpenRouter's OpenAI-compatible chat API with its free (":free") models, the spare writer when Gemini is out of
/// quota. Adapted from Flopômetro without retries: each model is asked once. The daily cap on free models
/// (account-wide, reset at 00:00 UTC) is recorded in <paramref name="quota"/>, and every free model is then skipped
/// without a request until it resets. The key goes in the Authorization header, never the URL.
/// </summary>
public sealed class OpenRouterClient(HttpClient http, string apiKey, IReadOnlyList<string> models, RunUsage usage, QuotaTracker? quota = null)
    : ILlmClient
{
    internal static readonly Uri Endpoint = new("https://openrouter.ai/api/v1/chat/completions");
    private readonly QuotaTracker _quota = quota ?? new QuotaTracker();

    /// <summary>The key of the account-wide daily cap on ":free" models.</summary>
    internal const string FreeModelsKey = "openrouter:free-models";

    internal static string QuotaKey(string model) => $"openrouter:{model}";

    public string Name => $"openrouter:{models[0]}";

    private DateTimeOffset? ExhaustedUntil(string model) =>
        _quota.ExhaustedUntil(QuotaKey(model))
        ?? (model.EndsWith(":free", StringComparison.Ordinal) ? _quota.ExhaustedUntil(FreeModelsKey) : null);

    public async Task<T> CompleteJsonAsync<T>(string system, string user, CancellationToken ct)
    {
        var schema = Json.SchemaFor<T>();
        var lastError = "no OpenRouter model configured";
        foreach (var model in models)
        {
            if (ExhaustedUntil(model) is { } until)
            {
                lastError = $"openrouter:{model} is out of its daily quota until {until.ToUniversalTime():yyyy-MM-dd HH:mm} UTC";
                continue;
            }
            var body = new JsonObject
            {
                ["model"] = model,
                ["temperature"] = 0.6,
                ["messages"] = new JsonArray(
                    new JsonObject
                    {
                        ["role"] = "system",
                        // Not every free model honors response_format, so the schema is also spelled out.
                        ["content"] = $"{system}\n\nReply with only a JSON object matching this JSON schema:\n{schema.ToJsonString()}",
                    },
                    new JsonObject { ["role"] = "user", ["content"] = user }),
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = typeof(T).Name, ["strict"] = true, ["schema"] = schema.DeepClone() },
                },
            };

            usage.TakeModelRequest("openrouter");
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Add("X-Title", "Minuto Util");

            HttpResponseMessage response;
            string text;
            try
            {
                response = await http.SendAsync(request, ct);
                text = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                lastError = $"openrouter:{model} failed: {ex.Message}";
                Log.Warn(lastError);
                continue;
            }
            using var _ = response;

            if (response.IsSuccessStatusCode && ExtractContent(text) is { } content)
            {
                Log.Info($"openrouter:{model} answered");
                try
                {
                    return LlmJson.Deserialize<T>(content);
                }
                catch (JsonException ex)
                {
                    lastError = $"openrouter:{model} returned malformed JSON: {ex.Message}";
                    Log.Warn(lastError);
                    continue;
                }
            }

            lastError = $"openrouter:{model} returned {(int)response.StatusCode}: {TextUtil.Truncate(text, 300)}";
            Log.Warn(lastError);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired)
                throw new LlmException(lastError);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && QuotaTracker.IsDailyQuota(text))
            {
                var accountWide = text.Contains("free-models-per-day", StringComparison.OrdinalIgnoreCase) && model.EndsWith(":free", StringComparison.Ordinal);
                _quota.MarkExhausted(accountWide ? FreeModelsKey : QuotaKey(model), QuotaTracker.NextUtcMidnight(_quota.Now), text);
            }
        }
        throw new LlmException($"No OpenRouter model answered. Last error: {lastError}");
    }

    /// <summary>The assistant message text, or null when the response carries an error or no content.</summary>
    private static string? ExtractContent(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("error", out _)) return null;
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
            var message = choices[0].GetProperty("message");
            return message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(content.GetString())
                ? content.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
