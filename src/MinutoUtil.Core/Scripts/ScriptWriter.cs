using System.Text;
using MinutoUtil.Core.Llm;
using MinutoUtil.Core.Sources;

namespace MinutoUtil.Core.Scripts;

/// <summary>A script and what the local checks found in it.</summary>
public sealed record WrittenScript(VideoScript Script, IReadOnlyList<string> Warnings, string Writer);

/// <summary>
/// Asks the model for the script once, grounded in the Wikipedia text, then checks and cleans the answer locally (no
/// model involved): dashes removed, word and section counts, digits left in the narration, and whether each section's
/// source quote really is in the source text. Only a script that is far off (too short, too long, too few sections)
/// costs a second request, and the run's <see cref="RunUsage"/> budget caps even that.
/// </summary>
public sealed class ScriptWriter(ILlmClient llm, string promptTemplate, WriterOptions options, string channelName)
{
    public async Task<WrittenScript> WriteAsync(string topic, IReadOnlyList<SourceArticle> sources, int maxSourceChars, CancellationToken ct)
    {
        var system = SystemPrompt();
        var user = UserPrompt(topic, sources, maxSourceChars, null);
        var script = Polish(await llm.CompleteJsonAsync<VideoScript>(system, user, ct), sources);
        var problem = HardProblem(script);
        if (problem is not null)
        {
            Log.Warn($"script needs a rewrite: {problem}");
            var retry = UserPrompt(topic, sources, maxSourceChars, $"Your previous answer was rejected: {problem}. Fix that and keep every other rule.");
            script = Polish(await llm.CompleteJsonAsync<VideoScript>(system, retry, ct), sources);
            if (HardProblem(script) is { } still) Log.Warn($"script still off after the rewrite, used anyway: {still}");
        }
        return new WrittenScript(script, Check(script, sources), llm.Name);
    }

    internal string SystemPrompt() => promptTemplate
        .Replace("{CHANNEL}", channelName)
        .Replace("{MIN_WORDS}", options.MinWords.ToString())
        .Replace("{MAX_WORDS}", options.MaxWords.ToString())
        .Replace("{MIN_SECTIONS}", options.MinSections.ToString())
        .Replace("{MAX_SECTIONS}", options.MaxSections.ToString());

    internal static string UserPrompt(string topic, IReadOnlyList<SourceArticle> sources, int maxSourceChars, string? feedback)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Topic of the channel series: {topic}.");
        sb.AppendLine($"Event: {string.Join(" / ", sources.Select(s => s.Title))}.");
        sb.AppendLine();
        foreach (var source in sources)
        {
            sb.AppendLine($"=== SOURCE TEXT ({source.Language}, Wikipedia: {source.Title}) ===");
            sb.AppendLine(WikipediaClient.Excerpt(source.Text, maxSourceChars));
            sb.AppendLine($"=== END OF SOURCE TEXT ({source.Language}) ===");
            sb.AppendLine();
        }
        sb.AppendLine("Write the script as JSON now.");
        if (feedback is not null) sb.AppendLine(feedback);
        return sb.ToString();
    }

    /// <summary>
    /// The local fixes, safe to run again on a saved script: <see cref="Clean"/>, then accents a model left out put back
    /// from the Portuguese source text (<see cref="AccentRestorer"/>).
    /// </summary>
    public static VideoScript Polish(VideoScript script, IReadOnlyList<SourceArticle> sources)
    {
        var cleaned = Clean(script);
        var portuguese = string.Join('\n', sources.Where(s => s.Language == "pt").Select(s => s.Text));
        var restored = AccentRestorer.Restore(cleaned, portuguese, out var changed);
        if (changed > 0) Log.Warn($"script: put back the accents of {changed} word(s) the writer left out");
        return restored;
    }

    /// <summary>Dashes out of everything a viewer reads or hears; blank queries and hashtag signs dropped.</summary>
    internal static VideoScript Clean(VideoScript script) => script with
    {
        Title = SpokenText.WithoutDashes(script.Title),
        Hook = SpokenText.WithoutDashes(script.Hook),
        EraStamp = SpokenText.WithoutDashes(script.EraStamp),
        PlaceStamp = SpokenText.WithoutDashes(script.PlaceStamp),
        Description = SpokenText.WithoutDashes(script.Description),
        Question = SpokenText.WithoutDashes(script.Question ?? "").Trim(),
        Hashtags = script.Hashtags.Select(h => h.Trim().TrimStart('#').Replace(" ", "")).Where(h => h.Length > 0).Distinct().ToList(),
        Sections = script.Sections.Select(s => s with
        {
            Text = SpokenText.WithoutDashes(s.Text),
            SearchEn = s.SearchEn.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).ToList(),
            SearchPt = s.SearchPt.Where(q => !string.IsNullOrWhiteSpace(q)).Select(q => q.Trim()).ToList(),
        }).ToList(),
    };

    /// <summary>A problem worth one more request, or null.</summary>
    internal string? HardProblem(VideoScript script)
    {
        if (string.IsNullOrWhiteSpace(script.Hook)) return "the hook is empty";
        if (script.Sections.Count < options.MinSections || script.Sections.Count > options.MaxSections)
            return $"{script.Sections.Count} sections, the rule is {options.MinSections} to {options.MaxSections}";
        if (script.Sections.Any(s => string.IsNullOrWhiteSpace(s.Text))) return "a section has no narration";
        var words = script.WordCount;
        if (words < options.MinWords - options.HardWordMargin || words > options.MaxWords + options.HardWordMargin)
            return $"{words} words in hook plus sections, the rule is {options.MinWords} to {options.MaxWords}";
        return null;
    }

    /// <summary>Soft problems, logged and saved with the script.</summary>
    public IReadOnlyList<string> Check(VideoScript script, IReadOnlyList<SourceArticle> sources)
    {
        var warnings = new List<string>();
        var words = script.WordCount;
        if (words < options.MinWords || words > options.MaxWords)
            warnings.Add($"{words} words, outside {options.MinWords}-{options.MaxWords}");
        foreach (var (text, index) in script.SpokenSections.Select((t, i) => (t, i)))
            if (SpokenText.HasDigits(text))
                warnings.Add($"section {index} has digits; the voice gets them spelled out");
        var folded = string.Join(" \n ", sources.Select(s => TextUtil.Fold(s.Text)));
        for (var i = 0; i < script.Sections.Count; i++)
        {
            var quote = TextUtil.Fold(script.Sections[i].SourceQuote);
            if (quote.Length < 20 || !folded.Contains(quote, StringComparison.Ordinal))
                warnings.Add($"section {i + 1}: source quote not found word for word in the source text: \"{TextUtil.Truncate(script.Sections[i].SourceQuote, 120)}\"");
            if (script.Sections[i].SearchEn.Count == 0) warnings.Add($"section {i + 1} has no English search queries");
        }
        return warnings;
    }
}
