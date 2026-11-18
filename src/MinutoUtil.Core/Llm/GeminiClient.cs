using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MinutoUtil.Core.Llm;

/// <summary>
/// How long a run waits for Gemini when every model is busy (503, another 5xx, a timeout or a dropped connection),
/// and how often it asks again in the meantime (<see cref="WriterOptions.GeminiBusyRetryMinutes"/>).
/// <paramref name="QuotaWait"/> (<see cref="WriterOptions.GeminiQuotaWaitMinutes"/>) is how long a run may also wait,
/// in all, for a model to come back from a quota lock when every model is locked; that wait does not count against
/// <paramref name="For"/>.
/// </summary>
public sealed record GeminiBusyRetry(TimeSpan For, TimeSpan Every, TimeSpan QuotaWait = default);

/// <summary>
/// Gemini API, free tier. Adapted from Flopômetro's client. The key's free quota is shared with the live Flopômetro
/// channel, so one pass asks each model once at most: a busy or failing model hands over to the next one, and a model
/// out of its daily quota is recorded in <paramref name="quota"/> and skipped without a request until it resets
/// (midnight Pacific time). By default a failed pass is the end (the spare writers take over). With
/// <paramref name="busyRetry"/> (Profissão Spoiler, which has no spare writers) a pass in which a model was busy is
/// asked again every few minutes for a while; those busy requests are counted apart from the run's budget, since
/// Gemini's overload spikes (2026-11-09 to 11) left whole runs with nothing but 503s. When every model is in a quota
/// lock, such a run can also wait for the first lock to end (<see cref="GeminiBusyRetry.QuotaWait"/>): on 2026-11-17
/// and 18 Profissão Spoiler's retries started minutes after a failure, found every model locked for the next hour and
/// failed without a request. The key goes in a header, never the URL, so it stays out of logs.
/// </summary>
public sealed class GeminiClient(HttpClient http, string apiKey, IReadOnlyList<string> models, RunUsage usage, QuotaTracker? quota = null,
    GeminiBusyRetry? busyRetry = null, TimeProvider? time = null)
    : ILlmClient
{
    private readonly QuotaTracker _quota = quota ?? new QuotaTracker();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    // The first busy pass of this run: the wait is for the whole run, not for each request (a rewrite included).
    private DateTimeOffset? _firstBusy;
    // Time this run has spent waiting for quota locks to end, bounded by GeminiBusyRetry.QuotaWait.
    private TimeSpan _quotaWaited;

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

        while (true)
        {
            var pass = await AskEachModelAsync<T>(json, ct);
            if (pass.Answered) return pass.Value!;
            if (busyRetry is null) throw new LlmException($"No Gemini model answered. Last error: {pass.LastError}");

            var now = _time.GetUtcNow();
            if (pass.Busy)
            {
                _firstBusy ??= now;
                var deadline = _firstBusy.Value + busyRetry.For;
                if (now + busyRetry.Every > deadline)
                    throw new LlmException($"No Gemini model answered in {busyRetry.For.TotalMinutes:0} minutes of trying. Last error: {pass.LastError}");

                Log.Warn($"every Gemini model is busy; asking again in {busyRetry.Every.TotalMinutes:0} minutes " +
                         $"(until {deadline.ToUniversalTime():HH:mm} UTC at most)");
                await Task.Delay(busyRetry.Every, _time, ct);
                continue;
            }

            // Not busy: every model is locked for its quota (or refused for another reason). Wait for the first lock
            // to end when the run's quota wait allows it, a minute late so the lock has surely passed.
            if (FirstUnlock() is not { } unlock)
                throw new LlmException($"No Gemini model answered. Last error: {pass.LastError}");
            var wait = unlock - now + TimeSpan.FromMinutes(1);
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            if (_quotaWaited + wait > busyRetry.QuotaWait)
                throw new LlmException($"No Gemini model answered. Last error: {pass.LastError}");

            Log.Warn($"every Gemini model is out of its quota; asking again at {(now + wait).ToUniversalTime():HH:mm} UTC");
            await Task.Delay(wait, _time, ct);
            _quotaWaited += wait;
            // The busy window does not run while the run waits for a quota.
            if (_firstBusy is { } first) _firstBusy = first + wait;
        }
    }

    /// <summary>When the first quota-locked model is free again, or null when no model is locked.</summary>
    private DateTimeOffset? FirstUnlock()
    {
        DateTimeOffset? first = null;
        foreach (var model in models)
            if (_quota.ExhaustedUntil(QuotaKey(model)) is { } until && (first is null || until < first))
                first = until;
        return first;
    }

    private readonly record struct Pass<T>(bool Answered, T? Value, bool Busy, string LastError);

    /// <summary>One request to each model in turn until one answers. Busy: at least one model failed for being busy.</summary>
    private async Task<Pass<T>> AskEachModelAsync<T>(string json, CancellationToken ct)
    {
        var lastError = "no Gemini model configured";
        var busy = false;
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
                busy = true;
                if (busyRetry is not null) usage.ReturnBusyGeminiRequest();
                continue;
            }
            using var _ = response;

            if (response.IsSuccessStatusCode)
            {
                Log.Info($"gemini:{model} answered");
                try
                {
                    return new Pass<T>(true, LlmJson.Deserialize<T>(ExtractText(text)), false, "");
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
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                busy = true;
                if (busyRetry is not null) usage.ReturnBusyGeminiRequest();
            }
        }
        return new Pass<T>(false, default, busy, lastError);
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
