using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MinutoUtil.Core.Llm;

/// <summary>
/// Gemini API, free tier. Adapted from Flopômetro's client with one difference: no retries. The key's free quota is
/// shared with the live Flopômetro channel, so each model gets one request at most; a busy or failing model hands over
/// to the next one, and a model out of its daily quota is recorded in <paramref name="quota"/> and skipped without a
/// request until it resets (midnight Pacific time). The key goes in a header, never the URL, so it stays out of logs.
/// </summary>
public sealed class GeminiClient(HttpClient http, string apiKey, IReadOnlyList<string> models, RunUsage usage, QuotaTracker? quota = null)
    : ILlmClient
{
    private readonly QuotaTracker _quota = quota ?? new QuotaTracker();

    internal static string QuotaKey(string model) => $"gemini:{model}";

    public string Name => $"gemini:{models[0]}";

    public async Task<T> CompleteJsonAsync<T>(string system, string user, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = user }),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0.6,
                ["responseMimeType"] = "application/json",
                ["responseJsonSchema"] = Json.SchemaFor<T>(),
            },
        };
        var json = body.ToJsonString();

        var lastError = "no Gemini model configured";
        foreach (var model in models)
        {
            if (_quota.ExhaustedUntil(QuotaKey(model)) is { } until)
            {
                lastError = $"gemini:{model} is out of its daily quota until {until.ToUniversalTime():yyyy-MM-dd HH:mm} UTC";
                Log.Info($"{lastError}, skipped without a request");
                continue;
            }
            usage.TakeModelRequest("gemini");
            var uri = new Uri($"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-goog-api-key", apiKey);

            HttpResponseMessage response;
            string text;
            try
            {
                response = await http.SendAsync(request, ct);
                text = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                lastError = $"gemini:{model} failed: {ex.Message}";
                Log.Warn(lastError);
                continue;
            }
            using var _ = response;

            if (response.IsSuccessStatusCode)
            {
                Log.Info($"gemini:{model} answered");
                try
                {
                    return LlmJson.Deserialize<T>(ExtractText(text));
                }
                catch (JsonException ex)
                {
                    // A malformed answer is not retried on the same model: the next one gets the request.
                    lastError = $"gemini:{model} returned malformed JSON: {ex.Message}";
                    Log.Warn(lastError);
                    continue;
                }
            }

            lastError = $"gemini:{model} returned {(int)response.StatusCode}: {TextUtil.Truncate(text, 300)}";
            Log.Warn(lastError);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new LlmException(lastError);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && QuotaTracker.IsDailyQuota(text))
                _quota.MarkExhausted(QuotaKey(model), QuotaTracker.NextPacificMidnight(_quota.Now), text);
        }
        throw new LlmException($"No Gemini model answered. Last error: {lastError}");
    }

    private static string ExtractText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            throw new JsonException($"Gemini returned no candidates: {TextUtil.Truncate(responseJson, 300)}");

        var builder = new StringBuilder();
        foreach (var part in candidates[0].GetProperty("content").GetProperty("parts").EnumerateArray())
        {
            if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
            if (part.TryGetProperty("text", out var t)) builder.Append(t.GetString());
        }
        return builder.ToString();
    }
}
