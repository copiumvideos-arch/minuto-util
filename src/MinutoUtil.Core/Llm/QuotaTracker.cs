using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinutoUtil.Core.Llm;

/// <summary>
/// Cloud models that ran out of their daily free quota, and when each quota resets. A model in here is skipped
/// without a request until then, so a run does not retry and wait on a model that will refuse every call until
/// tomorrow. One tracker is shared by every client in the process; with a <paramref name="path"/> it is also saved
/// (data/quota-state.json), so the next run knows too. Short rate limits (per minute) are never recorded.
/// Copied from Flopômetro (canal-dark), which shares the Gemini key: its quota file can be passed as
/// <paramref name="sharedPaths"/>, read only, so a model the live channel used up today is not asked here either.
/// </summary>
public sealed partial class QuotaTracker(string? path = null, TimeProvider? time = null, IReadOnlyList<string>? sharedPaths = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private Dictionary<string, DateTimeOffset>? _until;

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>When the quota of <paramref name="key"/> resets, or null when it is not known to be out.</summary>
    public DateTimeOffset? ExhaustedUntil(string key)
    {
        lock (_lock)
        {
            var state = State();
            if (!state.TryGetValue(key, out var until)) return null;
            if (until > Now) return until;
            state.Remove(key);
            Save(state);
            return null;
        }
    }

    /// <summary>Every model or key out of its quota right now, with when it may be asked again, soonest first.</summary>
    public IReadOnlyList<KeyValuePair<string, DateTimeOffset>> Active()
    {
        lock (_lock) return State().Where(s => s.Value > Now).OrderBy(s => s.Value).ToList();
    }

    /// <summary>
    /// How long a model is skipped at most, even when its quota resets later: a misread error then costs an hour, not
    /// a day (on 2026-10-22 two working Gemini models were locked out until the next day), and a model that really is
    /// out costs one request an hour to find out again.
    /// </summary>
    public static readonly TimeSpan MaxSkip = TimeSpan.FromHours(1);

    /// <param name="reason">The error body, kept short in the log so a wrong verdict can be traced.</param>
    public void MarkExhausted(string key, DateTimeOffset until, string reason = "")
    {
        if (until > Now + MaxSkip) until = Now + MaxSkip;
        lock (_lock)
        {
            var state = State();
            state[key] = until;
            Save(state);
        }
        Log.Warn($"{key} is out of its daily quota, skipped until {until.ToUniversalTime():yyyy-MM-dd HH:mm} UTC"
            + (reason.Length > 0 ? $" ({TextUtil.Truncate(System.Text.RegularExpressions.Regex.Replace(reason, @"\s+", " "), 300)})" : ""));
    }

    /// <summary>
    /// True when a 429 body says a daily quota ran out, false for a short rate limit. Gemini says "exceeded your current
    /// quota" for both and names the limit in a quota id ("...PerDay..." or "...PerMinute..."); OpenRouter's account-wide
    /// daily cap is "free-models-per-day", its short one "free-models-per-min".
    /// </summary>
    public static bool IsDailyQuota(string body)
    {
        // Only an explicit daily marker locks a model out until its reset. On 2026-10-22 Gemini's "exceeded your current
        // quota" without a quota id was taken as daily and all three models were skipped for the day while they still
        // answered; a wrong "short" verdict only costs a retry.
        var text = body.ToLowerInvariant();
        if (ShortMarker().IsMatch(text)) return false;
        return DailyMarker().IsMatch(text);
    }

    /// <summary>The Gemini free tier resets at midnight Pacific time.</summary>
    public static DateTimeOffset NextPacificMidnight(DateTimeOffset now)
    {
        var zone = Pacific();
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var midnight = local.Date.AddDays(1);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    /// <summary>OpenRouter's daily cap on free models resets at 00:00 UTC.</summary>
    public static DateTimeOffset NextUtcMidnight(DateTimeOffset now) =>
        new(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);

    private static TimeZoneInfo Pacific()
    {
        foreach (var id in (string[])["America/Los_Angeles", "Pacific Standard Time"])
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return zone;
        // Without time zone data, -8 h resets an hour late in summer, which only skips a model a little longer.
        return TimeZoneInfo.CreateCustomTimeZone("Pacific", TimeSpan.FromHours(-8), "Pacific", "Pacific");
    }

    private Dictionary<string, DateTimeOffset> State()
    {
        if (_until is not null) return _until;
        _until = [];
        foreach (var file in (sharedPaths ?? []).Prepend(path).OfType<string>())
        {
            if (!File.Exists(file)) continue;
            try
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(file)) ?? [];
                foreach (var (key, until) in saved.Where(s => s.Value > Now))
                    if (!_until.TryGetValue(key, out var known) || until > known) _until[key] = until;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken file only costs one request per model to learn the quota again.
                Log.Warn($"Could not read {file}, ignoring its quota limits: {ex.Message}");
            }
        }
        return _until;
    }

    private void Save(Dictionary<string, DateTimeOffset> state)
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not save {path}: {ex.Message}");
        }
    }

    [GeneratedRegex(@"per[-_ ]?day|daily")]
    private static partial Regex DailyMarker();

    [GeneratedRegex(@"per[-_ ]?min|per[-_ ]?second|per[-_ ]?hour")]
    private static partial Regex ShortMarker();
}
