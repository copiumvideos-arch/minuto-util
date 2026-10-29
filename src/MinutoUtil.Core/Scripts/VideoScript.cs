using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MinutoUtil.Core.Scripts;

/// <summary>One narration section and what to show while it is spoken.</summary>
public sealed record ScriptSection(
    [property: Description("Narration in Brazilian Portuguese: 1-4 sentences, one step of the explanation, numbers written out in words.")]
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
    [property: Description("The first spoken sentence: the question the video answers, or the strange fact behind it, that makes the viewer stay. At most 20 words.")]
    string Hook,
    [property: Description("Date shown on the first shot, Portuguese, digits allowed: \"6 de junho de 1944\".")]
    string EraStamp,
    [property: Description("Place shown under the date: \"Normandia, França\".")]
    string PlaceStamp,
    IReadOnlyList<ScriptSection> Sections,
    [property: Description("Upload description in Portuguese, 2-3 sentences, no hashtags, no credits (they are added). It is read, not spoken: years and numbers in digits (\"em 1912\").")]
    string Description,
    [property: Description("4-6 hashtags without the # sign, e.g. \"historia\", \"segundaguerra\".")]
    IReadOnlyList<string> Hashtags)
{
    /// <summary>
    /// The question to the viewer that opens the comments, added to the caption and description (and spoken at the end
    /// on subjects that divide opinions). Optional: scripts written before 2026-10-28 lack it.
    /// </summary>
    [Description("One short, open question to the viewer in Portuguese, at most 14 words, about this story, that people will want to answer in the comments: a choice or an opinion. Neutral: never suggests fraud or a conspiracy, never says one side is right, never names a current politician, party or candidate.")]
    public string Question { get; init; } = "";

    /// <summary>
    /// The words on the thumbnail (YouTube) and the TikTok cover, with the one word shown in red between asterisks.
    /// Optional: scripts written before 2026-10-28 lack it, and their thumbnail shows the title instead.
    /// </summary>
    [Description("Words on the video's cover image, Portuguese, 2 to 5 words that open the same gap as the hook in fewer words (\"O bilhete que *ninguém* leu\"). Put the one word that should be in red between asterisks. No digits unless it is a year, no emoji, no exclamation marks.")]
    public string ThumbnailText { get; init; } = "";

    /// <summary>
    /// What the AI painting behind the thumbnail text shows (style.thumbnailPicture "ai"), in English for the image
    /// model. Optional: without it, or when the painting fails, the thumbnail uses a picture from the video.
    /// </summary>
    [Description("One English sentence, at most 40 words, describing a scene for a painted cover picture of this story: the place, the time, objects, weather, light. People only as distant figures, silhouettes or seen from behind, never a named or famous person, no faces. No text, signs, flags with writing or logos. Example: \"A stormy English Channel at dawn in 1944, hundreds of grey landing craft heading to a cliffy beach, low clouds\".")]
    public string ThumbnailScene { get; init; } = "";

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
