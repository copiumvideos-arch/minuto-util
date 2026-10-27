using System.Text.Json;
using MinutoUtil.Core.Scripts;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Core.Voice;

public sealed record SpokenWord(string Text, double Start, double End, int Section);

public sealed record SectionTime(double Start, double End);

/// <summary>How well Whisper heard one section back: the share of its script words matched.</summary>
public sealed record AlignCheck(int Section, double Score, int Words);

/// <summary>When each word and section is spoken in voice.wav, and which voice spoke.</summary>
public sealed record SpeechTimings(double Duration, IReadOnlyList<SectionTime> Sections, IReadOnlyList<SpokenWord> Words,
    IReadOnlyList<AlignCheck> Checks, string Voice = "");

/// <summary>
/// Speaks the script section by section (Smallest.ai, Kokoro if that fails), then tools/voice.py joins the takes with
/// fixed pauses and times every word with faster-whisper, checked against the script like Flopômetro's align.py.
/// Captions keep the script's spelling; only the timing comes from Whisper.
/// </summary>
public sealed class Narrator(VoiceOptions options, string toolsDirectory, SmallestVoice? smallest, KokoroVoice kokoro, RunUsage usage)
{
    public async Task<SpeechTimings> NarrateAsync(VideoScript script, string runFolder, CancellationToken ct)
    {
        var captions = script.SpokenSections.Select(KokoroVoice.OneLine).ToList();
        // The voice hears numbers in words; the captions show the script as written.
        var spoken = captions.Select(SpokenText.SpellNumbers).ToList();

        var voiceFolder = Path.Combine(runFolder, "voice");
        IReadOnlyList<string> takes;
        string voice;
        if (options.Engine == "smallest" && smallest is not null)
        {
            try
            {
                takes = await smallest.SpeakAsync(spoken, Path.Combine(voiceFolder, "smallest"), ct);
                voice = smallest.Name;
            }
            catch (VoiceException ex)
            {
                Log.Warn($"{ex.Message}; falling back to {kokoro.Name}");
                takes = await kokoro.SpeakAsync(spoken, Path.Combine(voiceFolder, "kokoro"), ct);
                voice = kokoro.Name;
            }
        }
        else
        {
            if (options.Engine == "smallest") Log.Warn($"No Smallest.ai key; using {kokoro.Name}");
            takes = await kokoro.SpeakAsync(spoken, Path.Combine(voiceFolder, "kokoro"), ct);
            voice = kokoro.Name;
        }
        usage.Voice = voice;

        var timings = await CachedAssembleAsync(takes, captions, spoken, runFolder, voiceFolder, ct);
        foreach (var check in timings.Checks.Where(c => options.Listener == "whisper" && c.Score < options.MinAlignScore))
            Log.Warn($"Whisper heard only {check.Score:P0} of section {check.Section}'s words; listen to it before posting");
        return timings with { Voice = voice };
    }

    /// <summary>Reuses voice.wav and its timings when the takes and captions are the same as last time (Whisper takes a minute).</summary>
    private async Task<SpeechTimings> CachedAssembleAsync(IReadOnlyList<string> takes, IReadOnlyList<string> captions, IReadOnlyList<string> spoken,
        string runFolder, string voiceFolder, CancellationToken ct)
    {
        var wav = Path.Combine(runFolder, "voice.wav");
        var timingsPath = Path.Combine(voiceFolder, "speech-timings.json");
        var stampPath = Path.Combine(voiceFolder, "speech-timings.key");
        var key = string.Join('\n', takes.Select((t, i) => $"{Path.GetFullPath(t)}|{new FileInfo(t).Length}|{captions[i]}"))
                  + $"|{options.SectionPause}|{options.MaxPause}|{options.Listener}";
        if (File.Exists(wav) && File.Exists(timingsPath) && File.Exists(stampPath) && await File.ReadAllTextAsync(stampPath, ct) == key)
        {
            Log.Info("voice: reused voice.wav and its word timings");
            return JsonSerializer.Deserialize<SpeechTimings>(await File.ReadAllTextAsync(timingsPath, ct), Json.Options)!;
        }
        var timings = await AssembleAsync(takes, captions, spoken, wav, voiceFolder, ct, options.Listener);
        await File.WriteAllTextAsync(stampPath, key, ct);
        return timings;
    }

    /// <summary>Joins the takes into one WAV and times its words. <paramref name="listener"/> "none" spreads words evenly (tests only).</summary>
    public async Task<SpeechTimings> AssembleAsync(IReadOnlyList<string> takes, IReadOnlyList<string> captions, IReadOnlyList<string> spoken,
        string outputWav, string workFolder, CancellationToken ct, string listener = "whisper")
    {
        Directory.CreateDirectory(workFolder);
        var plan = Path.Combine(workFolder, "assemble.json");
        await File.WriteAllTextAsync(plan, JsonSerializer.Serialize(
            takes.Select((t, i) => new { audio = Path.GetFullPath(t), caption = captions[i], spoken = spoken[i] }), Json.Options), ct);
        var timingsPath = Path.Combine(workFolder, "speech-timings.json");
        await ProcessRunner.RunCheckedAsync("uv",
            ["run", "--quiet", "voice.py", "assemble", "--plan", Path.GetFullPath(plan), "--out", Path.GetFullPath(outputWav),
             "--timings", Path.GetFullPath(timingsPath), "--listener", listener,
             "--section-pause", Num(options.SectionPause), "--max-pause", Num(options.MaxPause)],
            toolsDirectory, TimeSpan.FromMinutes(15), ct);
        var timings = JsonSerializer.Deserialize<SpeechTimings>(await File.ReadAllTextAsync(timingsPath, ct), Json.Options)
            ?? throw new VoiceException("voice.py wrote empty timings.");
        if (timings.Words.Count == 0 || timings.Sections.Count != takes.Count)
            throw new VoiceException($"voice.py returned {timings.Words.Count} words and {timings.Sections.Count} sections for {takes.Count} takes.");
        return timings;
    }

    private static string Num(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
