using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Core.Voice;

public sealed class VoiceException(string message) : Exception(message);

/// <summary>
/// Smallest.ai Waves text to speech (voice "thiago", Brazilian Portuguese). One request per section, so the pauses
/// between sections are ours, and each take is cached next to its text: the free plan's credits are limited, and a
/// re-render reuses the takes instead of paying again. The key goes in the Authorization header only, never a URL or
/// the log.
/// </summary>
public sealed class SmallestVoice(HttpClient http, string apiKey, SmallestOptions options, RunUsage usage)
{
    private const int MaxAudioBytes = 30_000_000;

    public string Name => $"smallest:{options.VoiceId}";

    /// <summary>Writes section-NN.wav for each text into <paramref name="folder"/>, reusing takes of the same text.</summary>
    public async Task<IReadOnlyList<string>> SpeakAsync(IReadOnlyList<string> spokenTexts, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        for (var i = 0; i < spokenTexts.Count; i++)
        {
            var wav = Path.Combine(folder, $"section-{i:00}.wav");
            var stamp = Path.Combine(folder, $"section-{i:00}.json");
            var key = CacheKey(spokenTexts[i]);
            if (File.Exists(wav) && File.Exists(stamp) && await File.ReadAllTextAsync(stamp, ct) == key)
            {
                Log.Info($"{Name}: section {i} reused from the cache");
                paths.Add(wav);
                continue;
            }
            var audio = await RequestAsync(spokenTexts[i], ct);
            await File.WriteAllBytesAsync(wav, audio, ct);
            await File.WriteAllTextAsync(stamp, key, ct);
            Log.Info($"{Name}: section {i} spoken ({spokenTexts[i].Length} characters, {audio.Length / 1024} KB)");
            paths.Add(wav);
        }
        return paths;
    }

    /// <summary>What a cached take was made from; any change (text, voice, model, speed) makes a new take.</summary>
    internal string CacheKey(string text) => new JsonObject
    {
        ["engine"] = "smallest",
        ["voice"] = options.VoiceId,
        ["model"] = options.Model,
        ["speed"] = options.Speed,
        ["sampleRate"] = options.SampleRate,
        ["text"] = text,
    }.ToJsonString();

    internal JsonObject RequestBody(string text) => new()
    {
        ["text"] = text,
        ["voice_id"] = options.VoiceId,
        ["model"] = options.Model,
        ["language"] = options.Language,
        ["sample_rate"] = options.SampleRate,
        ["speed"] = options.Speed,
        ["output_format"] = "wav",
    };

    private async Task<byte[]> RequestAsync(string text, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
        {
            Content = new StringContent(RequestBody(text).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new VoiceException($"{Name} request failed: {ex.Message}");
        }
        using var _ = response;
        // Counted once the request reached the service: a failed answer may still have used credits.
        usage.AddSmallest(text.Length);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new VoiceException($"{Name} returned {(int)response.StatusCode}: {TextUtil.Truncate(body.Replace(apiKey, "***"), 300)}");
        }
        var audio = await SafeHttp.ReadBytesLimitedAsync(response, MaxAudioBytes, ct);
        if (!WavFile.LooksLikeWav(audio))
            throw new VoiceException($"{Name} answered {response.Content.Headers.ContentType?.MediaType ?? "no content type"}, not a WAV file ({audio.Length} bytes).");
        return audio;
    }
}

/// <summary>Kokoro pm_santa on the CPU through tools/voice.py: the voice when Smallest fails or runs out of credits.</summary>
public sealed class KokoroVoice(string toolsDirectory, VoiceOptions options)
{
    public string Name => $"kokoro:{options.KokoroVoice}";

    public async Task<IReadOnlyList<string>> SpeakAsync(IReadOnlyList<string> spokenTexts, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var textFile = Path.Combine(folder, "kokoro-input.txt");
        await File.WriteAllLinesAsync(textFile, spokenTexts.Select(OneLine), ct);
        await ProcessRunner.RunCheckedAsync("uv",
            ["run", "--quiet", "voice.py", "kokoro", "--text-file", Path.GetFullPath(textFile), "--out-dir", Path.GetFullPath(folder),
             "--voice", options.KokoroVoice, "--speed", options.KokoroSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            toolsDirectory, TimeSpan.FromMinutes(15), ct);
        var paths = spokenTexts.Select((_, i) => Path.Combine(folder, $"section-{i:00}.wav")).ToList();
        if (paths.FirstOrDefault(p => !File.Exists(p)) is { } missing) throw new VoiceException($"Kokoro did not write {missing}.");
        return paths;
    }

    internal static string OneLine(string text) => string.Join(' ', text.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}

/// <summary>The header check of a WAV file: "RIFF", size, "WAVE".</summary>
public static class WavFile
{
    public static bool LooksLikeWav(ReadOnlySpan<byte> data) =>
        data.Length > 44 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WAVE"u8);
}
