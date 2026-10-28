using System.Text.RegularExpressions;

namespace MinutoUtil.Core.Media;

public enum MediaKind { Image, Video }

/// <summary>
/// A picture or footage found by a search, with everything the credits need. <see cref="LocalPath"/> is set once the
/// file is downloaded and validated into the run's media folder.
/// </summary>
public sealed record MediaItem
{
    public required string Id { get; init; }
    public required MediaKind Kind { get; init; }

    /// <summary>"Wikimedia Commons", "Library of Congress", "Internet Archive (US National Archives)".</summary>
    public required string Source { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }

    /// <summary>A license from <see cref="LicensePolicy"/>: "Public domain", "CC0", "CC BY 4.0", "CC BY-SA 3.0".</summary>
    public required string License { get; init; }

    /// <summary>The page a viewer can open to check the file and its license.</summary>
    public required string SourceUrl { get; init; }
    public required string DownloadUrl { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double? DurationSeconds { get; init; }

    /// <summary>The year the picture was taken, when the metadata says.</summary>
    public int? Year { get; init; }

    /// <summary>The query that found it and the section it was found for.</summary>
    public string Query { get; init; } = "";
    public int Section { get; init; } = -1;
    public double Score { get; init; }

    public string? LocalPath { get; init; }

    /// <summary>For a video: where the clip was cut from the source, in seconds.</summary>
    public double? ClipStart { get; init; }

    /// <summary>64-bit difference hash of the downloaded picture, to drop near duplicates.</summary>
    public ulong? Hash { get; init; }

    public string Credit => $"{Title}. {Author}. {License}. {Source}: {SourceUrl}";
}

/// <summary>
/// Which licenses may be used: public domain, CC0, CC BY and CC BY-SA, any version. Anything with NC (non-commercial) or
/// ND (no derivatives, and every video here is a derivative: cropped, moved, captioned) is refused, as is anything
/// unknown.
/// </summary>
public static partial class LicensePolicy
{
    [GeneratedRegex(@"\bCC[ -]BY(-SA)?[ -]?(\d\.\d)?", RegexOptions.IgnoreCase)]
    private static partial Regex CreativeCommons();

    /// <summary>The license as shown in the credits, or null when it is not allowed.</summary>
    public static string? Classify(string? shortName, string? url = null)
    {
        var name = (shortName ?? "").Trim();
        var link = (url ?? "").Trim().ToLowerInvariant();
        var lower = name.ToLowerInvariant();
        if (lower.Contains("nc") && CreativeCommons().IsMatch(name) || lower.Contains("-nd") || lower.Contains(" nd")
            || link.Contains("-nc") || link.Contains("-nd") || link.Contains("/by-nc") || link.Contains("/by-nd"))
            return null;
        if (lower.StartsWith("public domain") || lower.StartsWith("pd") || lower == "pdm" || link.Contains("publicdomain/mark")
            || link.Contains("/licenses/publicdomain"))
            return "Public domain";
        if (lower.StartsWith("cc0") || link.Contains("publicdomain/zero")) return "CC0";
        var match = CreativeCommons().Match(name);
        if (match.Success)
            return $"CC BY{(match.Groups[1].Success ? "-SA" : "")}{(match.Groups[2].Success ? " " + match.Groups[2].Value : "")}";
        var fromUrl = Regex.Match(link, @"creativecommons\.org/licenses/(by(?:-sa)?)/(\d\.\d)");
        if (fromUrl.Success) return $"CC {fromUrl.Groups[1].Value.ToUpperInvariant()} {fromUrl.Groups[2].Value}";
        return null;
    }
}

/// <summary>Ranks search results: relevant (search order), old enough, sharp enough, and never graphic.</summary>
public static class MediaScoring
{
    public static bool IsBlocked(string text, IReadOnlyList<string> blockedWords)
    {
        var words = TextUtil.Fold(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return blockedWords.Any(b => words.Contains(TextUtil.Fold(b)));
    }

    /// <summary>
    /// The first photographs (daguerreotypes). Before it no picture shows the event as it happened, so a painting from
    /// 1850 or a photo of the ruins from 2010 is what an ancient or early modern story is told with, not a later stand-in.
    /// </summary>
    public const int PhotographyYear = 1839;

    public static double Score(MediaItem item, int rank, IReadOnlyList<string> penalizedWords, int? eventYear)
    {
        var score = 1.0 / (1 + rank * 0.15);
        if (item.Year is { } year && eventYear is { } target)
        {
            if (Math.Abs(year - target) <= 2) score += 0.35;
            // Before photography a later picture is all there is (paintings, statues, ruins), so age costs nothing.
            else if (target >= PhotographyYear && year >= target + 30) score -= 1.5; // a photo of today's site: only if nothing else exists
            else if (target >= PhotographyYear && year >= target + 10) score -= 0.5; // a memorial or museum photo, not the event
        }
        else if (eventYear >= PhotographyYear)
        {
            score -= 0.1; // undated: often a modern photo or a scan of a map (normal for an ancient artifact)
        }
        var words = TextUtil.Fold(item.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (penalizedWords.Any(p => words.Contains(TextUtil.Fold(p)))) score -= 0.6;
        if (item.Width > 0 && item.Height > 0)
        {
            var aspect = (double)item.Width / item.Height;
            if (aspect > 2.2) score -= 0.4; // panoramas lose almost everything in a 9:16 crop
            if (Math.Min(item.Width, item.Height) >= 1500) score += 0.1;
        }
        return Math.Round(score, 3);
    }

    /// <summary>
    /// The first words of a title, folded: "Meeting of the Supreme Command ... IWM TR 1631" and "... IWM TR 1636" are
    /// two frames of the same scene, which the picture hash does not catch when one is colorized or cropped.
    /// </summary>
    public static string TitleKey(string title) =>
        string.Join(' ', TextUtil.Fold(title).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4));

    /// <summary>The first plausible year (1800-2099) in a metadata date like "6 June 1944" or "1944-06-06".</summary>
    public static int? YearOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(TextUtil.StripHtml(text), @"(?<!\d)(18\d\d|19\d\d|20\d\d)(?!\d)");
        return match.Success ? int.Parse(match.Value) : null;
    }
}
