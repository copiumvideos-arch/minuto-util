using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MinutoUtil.Core.Scripts;

/// <summary>One narration section and what to show while it is spoken.</summary>
public sealed record ScriptSection(
    [property: Description("Narration in Brazilian Portuguese: 1-3 short sentences, numbers written out in words.")]
    string Text,
    [property: Description("2-3 English search queries for archive photos or footage of exactly what this section says, concrete nouns (\"Omaha Beach landing craft 1944\").")]
    IReadOnlyList<string> SearchEn,
    [property: Description("1-2 Portuguese search queries for the same pictures.")]
    IReadOnlyList<string> SearchPt,
    [property: Description("A sentence copied word for word from the source text that backs this section's facts.")]
    string SourceQuote);

/// <summary>What the writer returns: the narration, its pictures and the upload copy.</summary>
public sealed record VideoScript(
    [property: Description("Title for YouTube Shorts and TikTok, Portuguese, at most 70 characters, no hashtags.")]
    string Title,
    [property: Description("The first spoken sentence: a curious question or surprising fact that makes the viewer stay. At most 14 words.")]
    string Hook,
    [property: Description("Date shown on the first shot, Portuguese, digits allowed: \"6 de junho de 1944\".")]
    string EraStamp,
    [property: Description("Place shown under the date: \"Normandia, França\".")]
    string PlaceStamp,
    IReadOnlyList<ScriptSection> Sections,
    [property: Description("Upload description in Portuguese, 2-3 sentences, no hashtags, no credits (they are added).")]
    string Description,
    [property: Description("4-6 hashtags without the # sign, e.g. \"historia\", \"segundaguerra\".")]
    IReadOnlyList<string> Hashtags)
{
    /// <summary>Where the event happened, for the map shot. Optional: scripts written before 2026-10-27 lack it.</summary>
    [Description("Where the event happened, for a map: the place name in Portuguese as shown on screen (\"Normandia\") and its latitude and longitude in decimal degrees (west and south negative).")]
    public ScriptLocation? Location { get; init; }

    /// <summary>What the voice says, in order: the hook, then each section.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> SpokenSections => [Hook, .. Sections.Select(s => s.Text)];

    [JsonIgnore]
    public int WordCount => SpokenSections.Sum(TextUtil.CountWords);
}

public sealed record ScriptLocation(string Name, double Latitude, double Longitude);
