using System.Text.Json;

namespace MinutoUtil.Core.Llm;

public interface ILlmClient
{
    string Name { get; }

    /// <summary>Asks for a response matching the JSON schema of <typeparamref name="T"/>.</summary>
    Task<T> CompleteJsonAsync<T>(string system, string user, CancellationToken ct);
}

public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Asks each client in turn until one answers: Gemini's free quota runs out, and OpenRouter's free models keep the run
/// going. Unlike Flopômetro's chain there is no local model at the end: a script from a tiny model is not worth a video.
/// </summary>
public sealed class ChainedLlmClient(IReadOnlyList<ILlmClient> clients) : ILlmClient
{
    public string Name => string.Join(" > ", clients.Select(c => c.Name));

    public async Task<T> CompleteJsonAsync<T>(string system, string user, CancellationToken ct)
    {
        if (clients.Count == 0) throw new LlmException("No script writer is set up (no GEMINI_API_KEY or OPENROUTER_API_KEY).");
        for (var i = 0; ; i++)
        {
            try
            {
                return await clients[i].CompleteJsonAsync<T>(system, user, ct);
            }
            catch (Exception ex) when (i + 1 < clients.Count && ex is LlmException or HttpRequestException or JsonException
                                       || i + 1 < clients.Count && ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                Log.Warn($"{clients[i].Name} failed ({TextUtil.Truncate(ex.Message, 200)}), asking {clients[i + 1].Name}");
            }
        }
    }
}

internal static class LlmJson
{
    public static T Deserialize<T>(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline) text = text[(firstNewline + 1)..lastFence];
        }
        // Some free models wrap the object in prose; the outermost braces hold the answer.
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open > 0 && close > open) text = text[open..(close + 1)];
        return JsonSerializer.Deserialize<T>(text, Json.Options)
            ?? throw new JsonException("Model returned null.");
    }
}
