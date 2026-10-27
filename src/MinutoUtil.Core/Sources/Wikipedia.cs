using System.Text.Json;

namespace MinutoUtil.Core.Sources;

/// <summary>One Wikipedia article as plain text: the only facts the writer may use.</summary>
public sealed record SourceArticle(string Language, string Title, string Url, string Text, long RevisionId);

/// <summary>
/// Article text from the MediaWiki Action API (prop=extracts, plain text), with the descriptive User-Agent Wikimedia
/// asks bots for (<see cref="SafeHttp.UserAgent"/>). The Portuguese article is found through the English one's
/// language link, so both describe the same event.
/// </summary>
public sealed class WikipediaClient(HttpClient http)
{
    private const int MaxResponseBytes = 4_000_000;

    public async Task<SourceArticle> FetchAsync(string language, string title, CancellationToken ct)
    {
        var (article, _) = await QueryAsync(language, title, null, ct);
        return article;
    }

    /// <summary>The English article and its twin in <paramref name="otherLanguage"/> (taken from the language link unless given).</summary>
    public async Task<(SourceArticle English, SourceArticle? Other)> FetchPairAsync(
        string englishTitle, string otherLanguage, string? otherTitle, CancellationToken ct)
    {
        var (english, linked) = await QueryAsync("en", englishTitle, otherLanguage, ct);
        var title = string.IsNullOrWhiteSpace(otherTitle) ? linked : otherTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            Log.Warn($"\"{english.Title}\" has no {otherLanguage} article; the writer gets the English one only");
            return (english, null);
        }
        return (english, await FetchAsync(otherLanguage, title, ct));
    }

    private async Task<(SourceArticle Article, string? LinkedTitle)> QueryAsync(
        string language, string title, string? linkLanguage, CancellationToken ct)
    {
        var query = new Dictionary<string, string>
        {
            ["action"] = "query",
            ["format"] = "json",
            ["formatversion"] = "2",
            ["prop"] = linkLanguage is null ? "extracts|revisions" : "extracts|revisions|langlinks",
            ["explaintext"] = "1",
            ["exsectionformat"] = "plain",
            ["rvprop"] = "ids",
            ["redirects"] = "1",
            ["titles"] = title,
        };
        if (linkLanguage is not null) query["lllang"] = linkLanguage;
        var uri = new Uri($"https://{language}.wikipedia.org/w/api.php?" +
                          string.Join("&", query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}")));

        using var response = await http.GetAsync(uri, ct);
        var body = await SafeHttp.ReadLimitedAsync(response, MaxResponseBytes, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{language}.wikipedia.org returned {(int)response.StatusCode} for \"{title}\".");
        return Parse(language, body);
    }

    internal static (SourceArticle Article, string? LinkedTitle) Parse(string language, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var page = doc.RootElement.GetProperty("query").GetProperty("pages")[0];
        if (page.TryGetProperty("missing", out _))
            throw new InvalidDataException($"The {language} Wikipedia article \"{page.GetProperty("title").GetString()}\" does not exist.");
        var title = page.GetProperty("title").GetString()!;
        var text = page.TryGetProperty("extract", out var extract) ? extract.GetString() ?? "" : "";
        long revision = 0;
        if (page.TryGetProperty("revisions", out var revisions) && revisions.GetArrayLength() > 0)
            revision = revisions[0].GetProperty("revid").GetInt64();
        string? linked = null;
        if (page.TryGetProperty("langlinks", out var links) && links.GetArrayLength() > 0)
            linked = links[0].GetProperty("title").GetString();
        var url = $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";
        return (new SourceArticle(language, title, url, CleanExtract(text), revision), linked);
    }

    /// <summary>
    /// Drops the reference sections at the end ("See also", "References", "Ver também", "Referências"): they hold
    /// book titles, not facts about the event.
    /// </summary>
    internal static string CleanExtract(string text)
    {
        string[] tails = ["\nSee also\n", "\nReferences\n", "\nNotes\n", "\nVer também\n", "\nReferências\n", "\nNotas\n", "\nBibliografia\n", "\nLigações externas\n"];
        var cut = tails.Select(t => text.IndexOf(t, StringComparison.Ordinal)).Where(i => i > 0).DefaultIfEmpty(text.Length).Min();
        return text[..cut].Trim();
    }

    /// <summary>The start of the article, cut at a paragraph end, for the writer's prompt.</summary>
    public static string Excerpt(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var cut = text.LastIndexOf('\n', maxChars);
        return text[..(cut > maxChars / 2 ? cut : maxChars)].Trim();
    }
}
