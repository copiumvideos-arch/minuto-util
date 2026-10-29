using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MinutoUtil.Core.Llm;

namespace MinutoUtil.Core.Cloud;

/// <summary>
/// What Brazil talks about today: Google News Brazil's top headlines (the stories, the strongest signal), Google
/// Trends' daily searches (often football fixtures) and pt.wikipedia's most read pages.
/// </summary>
public sealed record TrendSignals(IReadOnlyList<string> Headlines, IReadOnlyList<string> Searches, IReadOnlyList<string> WikipediaReads)
{
    public static readonly Uri GoogleNewsFeed = new("https://news.google.com/rss?hl=pt-BR&gl=BR&ceid=BR:pt-419");
    public static readonly Uri GoogleTrendsFeed = new("https://trends.google.com/trending/rss?geo=BR");

    public static Uri WikipediaTop(DateOnly day) =>
        new($"https://wikimedia.org/api/rest_v1/metrics/pageviews/top/pt.wikipedia/all-access/{day:yyyy}/{day:MM}/{day:dd}");

    public bool IsEmpty => Headlines.Count == 0 && Searches.Count == 0 && WikipediaReads.Count == 0;

    /// <summary>Both sources, each optional: one failing leaves the other. Everything is untrusted text, cut short.</summary>
    public static async Task<TrendSignals> FetchAsync(HttpClient http, DateOnly yesterday, CancellationToken ct)
    {
        var headlines = new List<string>();
        try
        {
            foreach (var item in (await ReadFeedAsync(http, GoogleNewsFeed, ct)).Descendants("item").Take(30))
                if (Clean(item.Element("title")?.Value, 160) is { } title) headlines.Add(title);
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"trends: Google News not read ({TextUtil.Truncate(ex.Message, 120)})");
        }

        var searches = new List<string>();
        try
        {
            XNamespace ht = "https://trends.google.com/trending/rss";
            foreach (var item in (await ReadFeedAsync(http, GoogleTrendsFeed, ct)).Descendants("item").Take(20))
            {
                var title = Clean(item.Element("title")?.Value, 60);
                if (title is null) continue;
                // The first headline says why the term is searched ("menino" alone means nothing).
                var news = Clean(item.Element(ht + "news_item")?.Element(ht + "news_item_title")?.Value, 140);
                searches.Add(news is null ? title : $"{title}: {news}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"trends: Google Trends not read ({TextUtil.Truncate(ex.Message, 120)})");
        }

        var reads = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(WikipediaTop(yesterday), ct));
            foreach (var article in doc.RootElement.GetProperty("items")[0].GetProperty("articles").EnumerateArray())
            {
                var name = article.GetProperty("article").GetString() ?? "";
                // Namespaced pages (Wikipédia:Página principal, Especial:Pesquisar) are not subjects.
                if (name.Contains(':')) continue;
                if (Clean(name.Replace('_', ' '), 80) is { } clean) reads.Add(clean);
                if (reads.Count == 30) break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                                       or IndexOutOfRangeException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"trends: Wikipedia top reads not read ({TextUtil.Truncate(ex.Message, 120)})");
        }
        return new TrendSignals(headlines, searches, reads);
    }

    /// <summary>An RSS feed as untrusted XML: no DTD, no external entities.</summary>
    private static async Task<XDocument> ReadFeedAsync(HttpClient http, Uri feed, CancellationToken ct)
    {
        var xml = await http.GetStringAsync(feed, ct);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }

    private static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var flat = new string(text.Where(c => !char.IsControl(c)).ToArray()).Replace('<', ' ').Replace('>', ' ').Trim();
        return flat.Length == 0 ? null : TextUtil.Truncate(flat, max);
    }
}

public sealed record TrendIdeas(IReadOnlyList<TrendIdea> Ideas);

public sealed record TrendIdea(
    [property: Description("The event's English Wikipedia article title, exactly as on Wikipedia, e.g. \"Magna Carta\".")]
    string Article,
    [property: Description("The Portuguese Wikipedia article on the same subject, exact title, when you know it (it can differ from the English page's language link, e.g. \"Urna eletrônica (Brasil)\"); otherwise an empty string.")]
    string PortugueseArticle,
    [property: Description("The year the event happened, negative before Christ (-490 for 490 BC), never 0.")]
    int Year,
    [property: Description("Short series name in Portuguese, e.g. \"Democracia\", \"Monarquias\", \"Grandes descobertas\".")]
    string Topic,
    [property: Description("The search, article or idea from the lists that inspired it, in Portuguese, a few words.")]
    string Trend);

/// <summary>The day's idea list sent to the owner on Telegram, kept as ideas/&lt;yyyyMMdd&gt;.json on the state branch.</summary>
public sealed record IdeaList(string Date, DateTimeOffset SentAt, IReadOnlyList<BacklogTopic> Ideas)
{
    public static string DateFor(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyyMMdd");
}

/// <summary>
/// An idea the owner wants made: one tapped in the day's list, or one sent as text (<see cref="FromText"/>), which goes
/// ahead of the tapped ones. Kept as approved/&lt;id&gt;.json, one file each, so taps at the same time never conflict.
/// </summary>
public sealed record ApprovedIdea(string Id, BacklogTopic Topic, bool FromText, DateTimeOffset ApprovedAt);

/// <summary>
/// Turns today's trends (or the owner's idea) into historical topics: one model request, then every pick is checked on
/// Wikipedia (the English article exists and is not a disambiguation page; the Portuguese article named by the model,
/// or else the English page's link, becomes the second source) and against the channel's history and rules.
/// The model's answer is only a suggestion; nothing it says is used without these checks.
/// </summary>
/// <param name="minYearsAgo">How old a pick must be at least (story.minYearsAgo): 25 years for history, 0 for games.</param>
public sealed class TrendPicker(ILlmClient llm, HttpClient http, string promptTemplate, string channelName, TimeProvider time, int minYearsAgo = 25)
{
    /// <summary>Ideas in the daily list sent to the owner.</summary>
    public const int Count = 10;
    /// <summary>The earliest year a pick may have: the first pharaohs (negative years are before Christ).</summary>
    public const int MinYear = -3200;

    public Task<IReadOnlyList<BacklogTopic>> PickAsync(TrendSignals signals, IReadOnlyCollection<string> alreadyMade, CancellationToken ct) =>
        PickFromAsync(UserPrompt(signals, alreadyMade), alreadyMade, null, ct);

    /// <summary>
    /// The owner's own idea, sent with /novo on Telegram: a subject or a question ("por que só o Brasil usa urna
    /// eletrônica?") turned into the event or history that answers it, checked like the trend picks.
    /// </summary>
    /// <remarks>
    /// The idea is first searched on Portuguese and English Wikipedia and the result titles go into the prompt: a model
    /// guessing titles from memory picked a nonexistent article and then an unrelated one (Thomas Edison for "urna
    /// eletrônica"), while the search finds "Urna eletrônica (Brasil)" at once.
    /// </remarks>
    public async Task<IReadOnlyList<BacklogTopic>> PickForIdeaAsync(string idea, IReadOnlyCollection<string> alreadyMade, CancellationToken ct)
    {
        var portuguese = await SearchAsync("pt", idea, ct);
        var english = await SearchAsync("en", idea, ct);
        return await PickFromAsync(IdeaPrompt(idea, portuguese, english, alreadyMade), alreadyMade, $"sua ideia: {TextUtil.Truncate(idea.Trim(), 70)}", ct);
    }

    /// <summary>The titles Wikipedia's own search finds for the text; none when the search fails.</summary>
    internal async Task<IReadOnlyList<string>> SearchAsync(string language, string text, CancellationToken ct)
    {
        try
        {
            var query = Publishing.OAuthRedirect.Query([
                new("action", "query"), new("format", "json"), new("formatversion", "2"), new("list", "search"),
                new("srsearch", TextUtil.Truncate(text.Trim(), 200)), new("srlimit", "8"), new("srprop", ""),
            ]);
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://{language}.wikipedia.org/w/api.php?{query}", ct));
            return doc.RootElement.GetProperty("query").GetProperty("search").EnumerateArray()
                .Select(r => r.GetProperty("title").GetString() ?? "").Where(t => t.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"idea: {language}.wikipedia search failed ({TextUtil.Truncate(ex.Message, 120)})");
            return [];
        }
    }

    private async Task<IReadOnlyList<BacklogTopic>> PickFromAsync(string user, IReadOnlyCollection<string> alreadyMade, string? trend, CancellationToken ct)
    {
        var system = promptTemplate.Replace("{CHANNEL}", channelName).Replace("{COUNT}", Count.ToString());
        var answer = await llm.CompleteJsonAsync<TrendIdeas>(system, user, ct);
        var latestYear = time.GetUtcNow().Year - minYearsAgo;
        var ideas = answer.Ideas
            .Where(i => !string.IsNullOrWhiteSpace(i.Article) && i.Article.Length <= 120 && (i.PortugueseArticle ?? "").Length <= 120
                        && i.Year != 0 && i.Year >= MinYear && i.Year <= latestYear)
            .Take(Count * 2).ToList();
        foreach (var dropped in answer.Ideas.Except(ideas)) Log.Warn($"trends: dropped \"{TextUtil.Truncate(dropped.Article, 80)}\" ({dropped.Year}): outside the rules");
        if (ideas.Count == 0) return [];

        var english = await ResolveAsync("en", ideas.Select(i => i.Article.Trim()).ToList(), "pt", ct);
        var askedPt = ideas.Select(i => (i.PortugueseArticle ?? "").Trim()).Where(t => t.Length > 0).Distinct().ToList();
        var portuguese = askedPt.Count > 0 ? await ResolveAsync("pt", askedPt, null, ct) : [];
        var picks = new List<BacklogTopic>();
        foreach (var idea in ideas)
        {
            if (!english.TryGetValue(idea.Article.Trim(), out var page))
            {
                Log.Warn($"trends: dropped \"{TextUtil.Truncate(idea.Article, 80)}\": no such English article");
                continue;
            }
            // The Portuguese article the model named wins over the English page's language link: the link is often
            // missing or points at a broader subject. Without either the writer gets the English text only.
            var pt = portuguese.TryGetValue((idea.PortugueseArticle ?? "").Trim(), out var named) ? named.Title : page.Linked;
            if (alreadyMade.Contains(page.Title, StringComparer.OrdinalIgnoreCase) || picks.Any(p => p.Article == page.Title)) continue;
            picks.Add(new BacklogTopic(page.Title, idea.Year, TextUtil.Truncate(idea.Topic.Trim(), 40),
                trend ?? TextUtil.Truncate(idea.Trend.Trim(), 80), pt));
            Log.Info($"trends: {page.Title} ({idea.Year}), pt {pt ?? "none"}, from \"{trend ?? idea.Trend}\"");
        }
        return picks.Take(Count).ToList();
    }

    internal static string IdeaPrompt(string idea, IReadOnlyList<string> portugueseResults, IReadOnlyList<string> englishResults,
        IReadOnlyCollection<string> alreadyMade)
    {
        var text = new StringBuilder();
        text.AppendLine("No trend lists this time: the channel owner sent this idea. Find the events or history that best answer or");
        text.AppendLine("explain it, following every rule; the first pick should fit the idea most closely. Prefer articles from the");
        text.AppendLine("Wikipedia search results below, with their exact titles: a Portuguese result goes in `portugueseArticle`,");
        text.AppendLine("and `article` must still be an English Wikipedia title (the English article on the same subject).");
        text.AppendLine();
        text.AppendLine($"Idea: {idea.Trim()}");
        text.AppendLine();
        text.AppendLine("Portuguese Wikipedia search results:");
        foreach (var r in portugueseResults) text.AppendLine($"- {r}");
        text.AppendLine();
        text.AppendLine("English Wikipedia search results:");
        foreach (var r in englishResults) text.AppendLine($"- {r}");
        text.AppendLine();
        text.AppendLine("Already made (do not pick):");
        foreach (var a in alreadyMade) text.AppendLine($"- {a}");
        return text.ToString();
    }

    internal static string UserPrompt(TrendSignals signals, IReadOnlyCollection<string> alreadyMade)
    {
        var text = new StringBuilder();
        text.AppendLine("Top news in Brazil today (Google News headlines, the strongest signal):");
        foreach (var h in signals.Headlines) text.AppendLine($"- {h}");
        text.AppendLine();
        text.AppendLine("Searched in Brazil today (Google Trends, with a headline):");
        foreach (var s in signals.Searches) text.AppendLine($"- {s}");
        text.AppendLine();
        text.AppendLine("Most read on Portuguese Wikipedia yesterday:");
        foreach (var r in signals.WikipediaReads) text.AppendLine($"- {r}");
        text.AppendLine();
        text.AppendLine("Already made (do not pick):");
        foreach (var a in alreadyMade) text.AppendLine($"- {a}");
        return text.ToString();
    }

    /// <summary>
    /// Maps each asked title to its canonical page in <paramref name="language"/> Wikipedia when the article exists and
    /// is not a disambiguation page, with its <paramref name="linkLanguage"/> twin's title when it has one.
    /// </summary>
    internal async Task<Dictionary<string, (string Title, string? Linked)>> ResolveAsync(string language, IReadOnlyList<string> titles,
        string? linkLanguage, CancellationToken ct)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("action", "query"), new("format", "json"), new("formatversion", "2"), new("redirects", "1"),
            new("prop", linkLanguage is null ? "pageprops" : "langlinks|pageprops"), new("ppprop", "disambiguation"),
            new("titles", string.Join('|', titles)),
        };
        if (linkLanguage is not null) parameters.Add(new("lllang", linkLanguage));
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://{language}.wikipedia.org/w/api.php?{Publishing.OAuthRedirect.Query(parameters)}", ct));
        var q = doc.RootElement.GetProperty("query");
        var map = titles.Distinct().ToDictionary(t => t, t => t);
        foreach (var key in new[] { "normalized", "redirects" })
            if (q.TryGetProperty(key, out var steps))
                foreach (var step in steps.EnumerateArray())
                {
                    string from = step.GetProperty("from").GetString()!, to = step.GetProperty("to").GetString()!;
                    foreach (var asked in map.Where(m => m.Value == from).Select(m => m.Key).ToList()) map[asked] = to;
                }
        var good = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var page in q.GetProperty("pages").EnumerateArray())
        {
            if (page.TryGetProperty("missing", out _) || page.TryGetProperty("invalid", out _)) continue;
            if (page.TryGetProperty("pageprops", out var props) && props.TryGetProperty("disambiguation", out _)) continue;
            var linked = page.TryGetProperty("langlinks", out var links) && links.GetArrayLength() > 0 ? links[0].GetProperty("title").GetString() : null;
            good[page.GetProperty("title").GetString()!] = linked;
        }
        return map.Where(m => good.ContainsKey(m.Value)).ToDictionary(m => m.Key, m => (m.Value, good[m.Value]));
    }
}
