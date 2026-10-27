using System.Text;

namespace MinutoUtil.Core.Publishing;

/// <summary>Turns a run's metadata.json into what each platform gets: YouTube's video details and the TikTok caption.</summary>
public static class PublishCopy
{
    public const int MaxTitleChars = 100;

    /// <summary>YouTube counts the description in UTF-8 bytes, not characters (accents take two).</summary>
    public const int MaxDescriptionBytes = 5000;

    /// <summary>YouTube's limit for all tags together.</summary>
    public const int MaxTagChars = 500;

    private const string ShortsTag = " #shorts";

    public static VideoDetails YouTube(VideoMetadata metadata, YouTubeOptions options) => new(
        Title(metadata.Title, options.AppendShortsTag),
        Description(metadata),
        Tags(metadata.Hashtags),
        options.CategoryId,
        options.PrivacyStatus,
        options.MadeForKids,
        options.ContainsSyntheticMedia,
        options.Language,
        options.AudioLanguage);

    /// <summary>At most 100 characters, cut at a word; " #shorts" is added only when it fits whole.</summary>
    public static string Title(string title, bool appendShortsTag)
    {
        var clean = Clean(title).Replace('\n', ' ').Trim();
        if (clean.Length > MaxTitleChars)
        {
            var cut = clean.LastIndexOf(' ', MaxTitleChars);
            clean = (cut >= MaxTitleChars * 2 / 3 ? clean[..cut] : clean[..MaxTitleChars]).TrimEnd(' ', ',', ':', ';', '-');
        }
        if (clean.Length == 0) throw new InvalidDataException("metadata.json has no title.");
        return appendShortsTag && clean.Length + ShortsTag.Length <= MaxTitleChars ? clean + ShortsTag : clean;
    }

    /// <summary>
    /// The description as written, with Windows line ends made plain and no angle brackets (YouTube refuses them).
    /// When it is over 5000 bytes, public domain and CC0 credit lines (no attribution required) are folded into one
    /// line; CC BY and CC BY-SA credits are required by their licenses and are never dropped: then it refuses instead.
    /// </summary>
    public static string Description(VideoMetadata metadata)
    {
        var description = Clean(metadata.Description);
        if (Bytes(description) <= MaxDescriptionBytes) return description;

        var optional = metadata.Media
            .Where(m => m.License.StartsWith("Public domain", StringComparison.OrdinalIgnoreCase) || m.License.Equals("CC0", StringComparison.OrdinalIgnoreCase))
            .Select(m => Clean($"- {m.Title}. {m.Author}. {m.License}. {m.Source}: {m.SourceUrl}"))
            .ToHashSet();
        var lines = description.Split('\n').ToList();
        var dropped = 0;
        for (var i = lines.Count - 1; i >= 0 && Bytes(string.Join('\n', lines)) + 60 > MaxDescriptionBytes; i--)
        {
            if (!optional.Contains(lines[i])) continue;
            var anchor = i;
            lines.RemoveAt(i);
            dropped++;
            // Keeps a running "and N more" line where the last dropped credit was.
            var summary = $"- Mais {dropped} {(dropped == 1 ? "item" : "itens")} de domínio público.";
            var existing = lines.FindIndex(l => l.StartsWith("- Mais ", StringComparison.Ordinal) && l.EndsWith(" de domínio público.", StringComparison.Ordinal));
            if (existing >= 0) lines[existing] = summary;
            else lines.Insert(Math.Min(anchor, lines.Count), summary);
        }
        var trimmed = string.Join('\n', lines);
        if (Bytes(trimmed) > MaxDescriptionBytes)
            throw new InvalidDataException($"The description is {Bytes(trimmed)} bytes after folding the public domain credits; YouTube allows {MaxDescriptionBytes}. Shorten metadata.json's description by hand.");
        Log.Info($"description over {MaxDescriptionBytes} bytes: {dropped} public domain credits folded into one line");
        return trimmed;
    }

    /// <summary>The hashtags as tags (no "#"), in order, until YouTube's 500-character total is reached.</summary>
    public static IReadOnlyList<string> Tags(IReadOnlyList<string> hashtags)
    {
        var tags = new List<string>();
        var used = 0;
        foreach (var tag in hashtags.Select(h => Clean(h).Trim().TrimStart('#').Replace(",", "")).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // YouTube counts a tag with a space as if it had quotes around it, and a comma between tags.
            var cost = tag.Length + (tag.Contains(' ') ? 2 : 0) + (tags.Count > 0 ? 1 : 0);
            if (used + cost > MaxTagChars) break;
            tags.Add(tag);
            used += cost;
        }
        return tags;
    }

    /// <summary>
    /// The TikTok caption: the title, a blank line, the hashtags. The inbox upload has no caption field, so this goes to
    /// tiktok-caption.txt for the owner to paste. The title is cut first if the whole would be over the limit.
    /// </summary>
    /// <summary>
    /// The caption to paste on TikTok: the title, the description's first paragraph (what the video tells, written by
    /// the script writer from the same facts), then the video's own hashtags followed by the broad discovery tags,
    /// deduplicated and without accents. The description is shortened first when the caption is too long.
    /// </summary>
    public static string TikTokCaption(VideoMetadata metadata, TikTokOptions options)
    {
        var maxChars = options.MaxCaptionChars;
        var own = metadata.Hashtags.Select(Hashtag).Where(h => h.Length > 0).Distinct().Take(options.MaxOwnHashtags);
        var tags = own.Concat(options.DiscoveryHashtags.Select(Hashtag)).Distinct().Take(options.MaxHashtags).Select(h => "#" + h);
        var hashtags = string.Join(' ', tags);
        var title = Clean(metadata.Title).Replace('\n', ' ').Trim();
        var room = maxChars - (hashtags.Length > 0 ? hashtags.Length + 2 : 0);
        if (room < 1) return TextUtil.Truncate(hashtags, maxChars);
        if (title.Length > room) title = title[..room].TrimEnd();
        var summary = Clean(metadata.Description).Split("\n\n")[0].Replace('\n', ' ').Trim();
        if (summary.StartsWith("Fonte:", StringComparison.Ordinal)) summary = "";
        var summaryRoom = room - title.Length - 2;
        if (summary.Length > summaryRoom) summary = summaryRoom > 40 ? summary[..(summaryRoom - 3)].TrimEnd() + "..." : "";
        var text = summary.Length > 0 ? $"{title}\n\n{summary}" : title;
        return hashtags.Length > 0 ? $"{text}\n\n{hashtags}" : text;
    }

    /// <summary>"Segunda Guerra" or "#história" becomes "segundaguerra" / "historia": letters and digits only.</summary>
    private static string Hashtag(string tag)
    {
        var plain = new string(tag.Normalize(NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        return new string(plain.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    private static string Clean(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("<", "").Replace(">", "").Trim();

    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);
}
