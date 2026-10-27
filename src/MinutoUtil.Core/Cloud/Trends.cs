using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MinutoUtil.Core.Llm;

namespace MinutoUtil.Core.Cloud;

/// <summary>What people in Brazil search for and read today: Google Trends' daily searches and pt.wikipedia's most read pages.</summary>
public sealed record TrendSignals(IReadOnlyList<string> Searches, IReadOnlyList<string> WikipediaReads)
{
    public static readonly Uri GoogleTrendsFeed = new("https://trends.google.com/trending/rss?geo=BR");

    public static Uri WikipediaTop(DateOnly day) =>
        new($"https://wikimedia.org/api/rest_v1/metrics/pageviews/top/pt.wikipedia/all-access/{day:yyyy}/{day:MM}/{day:dd}");

    public bool IsEmpty => Searches.Count == 0 && WikipediaReads.Count == 0;

    /// <summary>Both sources, each optional: one failing leaves the other. Everything is untrusted text, cut short.</summary>
    public static async Task<TrendSignals> FetchAsync(HttpClient http, DateOnly yesterday, CancellationToken ct)
    {
        var searches = new List<string>();
        try
        {
            var xml = await http.GetStringAsync(GoogleTrendsFeed, ct);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            XNamespace ht = "https://trends.google.com/trending/rss";
            foreach (var item in XDocument.Load(reader).Descendants("item").Take(20))
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
        return new TrendSignals(searches, reads);
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
    [property: Description("The year the event happened, 1 or later.")]
    int Year,
    [property: Description("Short series name in Portuguese, e.g. \"Democracia\", \"Monarquias\", \"Grandes descobertas\".")]
    string Topic,
    [property: Description("The search or article from today's lists that inspired it, in Portuguese, a few words.")]
    string Trend);

/// <summary>Today's trend picks waiting to be rendered, kept as trending.json on the state branch.</summary>
public sealed record TrendQueue(DateTimeOffset UpdatedAt, IReadOnlyList<BacklogTopic> Topics)
{
    /// <summary>A trend is old news after this; its picks give way to the backlog.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(3);

    /// <summary>A new scan at most this often: one model request a day.</summary>
    public static readonly TimeSpan ScanEvery = TimeSpan.FromHours(20);
}

/// <summary>
/// Turns today's trends into historical topics: one model request, then every pick is checked on Wikipedia (the
/// article exists, is not a disambiguation page, has a Portuguese twin) and against the channel's history and rules.
/// The model's answer is only a suggestion; nothing it says is used without these checks.
/// </summary>
public sealed class TrendPicker(ILlmClient llm, HttpClient http, string promptTemplate, string channelName, TimeProvider time)
{
    public const int Count = 4;
    public const int MinYearsAgo = 25;

    public async Task<IReadOnlyList<BacklogTopic>> PickAsync(TrendSignals signals, IReadOnlyCollection<string> alreadyMade, CancellationToken ct)
    {
        var system = promptTemplate.Replace("{CHANNEL}", channelName).Replace("{COUNT}", Count.ToString());
        var answer = await llm.CompleteJsonAsync<TrendIdeas>(system, UserPrompt(signals, alreadyMade), ct);
        var latestYear = time.GetUtcNow().Year - MinYearsAgo;
        var ideas = answer.Ideas
            .Where(i => !string.IsNullOrWhiteSpace(i.Article) && i.Article.Length <= 120 && i.Year >= 1 && i.Year <= latestYear)
            .Take(Count * 2).ToList();
        foreach (var dropped in answer.Ideas.Except(ideas)) Log.Warn($"trends: dropped \"{TextUtil.Truncate(dropped.Article, 80)}\" ({dropped.Year}): outside the rules");
        if (ideas.Count == 0) return [];

        var resolved = await ResolveAsync(ideas.Select(i => i.Article.Trim()).ToList(), ct);
        var picks = new List<BacklogTopic>();
        foreach (var idea in ideas)
        {
            if (!resolved.TryGetValue(idea.Article.Trim(), out var title))
            {
                Log.Warn($"trends: dropped \"{TextUtil.Truncate(idea.Article, 80)}\": no English article with a Portuguese twin");
                continue;
            }
            if (alreadyMade.Contains(title, StringComparer.OrdinalIgnoreCase) || picks.Any(p => p.Article == title)) continue;
            picks.Add(new BacklogTopic(title, idea.Year, TextUtil.Truncate(idea.Topic.Trim(), 40), TextUtil.Truncate(idea.Trend.Trim(), 80)));
            Log.Info($"trends: {title} ({idea.Year}), from \"{idea.Trend}\"");
        }
        return picks.Take(Count).ToList();
    }

    internal static string UserPrompt(TrendSignals signals, IReadOnlyCollection<string> alreadyMade)
    {
        var text = new StringBuilder();
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

    /// <summary>Maps each asked title to the canonical English title when the article exists, is not a disambiguation page and has a Portuguese twin.</summary>
    internal async Task<Dictionary<string, string>> ResolveAsync(IReadOnlyList<string> titles, CancellationToken ct)
    {
        var query = Publishing.OAuthRedirect.Query([
            new("action", "query"), new("format", "json"), new("formatversion", "2"), new("redirects", "1"),
            new("prop", "langlinks|pageprops"), new("lllang", "pt"), new("ppprop", "disambiguation"), new("titles", string.Join('|', titles)),
        ]);
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://en.wikipedia.org/w/api.php?{query}", ct));
        var q = doc.RootElement.GetProperty("query");
        var map = titles.ToDictionary(t => t, t => t);
        foreach (var key in new[] { "normalized", "redirects" })
            if (q.TryGetProperty(key, out var steps))
                foreach (var step in steps.EnumerateArray())
                {
                    string from = step.GetProperty("from").GetString()!, to = step.GetProperty("to").GetString()!;
                    foreach (var asked in map.Where(m => m.Value == from).Select(m => m.Key).ToList()) map[asked] = to;
                }
        var good = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in q.GetProperty("pages").EnumerateArray())
        {
            if (page.TryGetProperty("missing", out _) || page.TryGetProperty("invalid", out _)) continue;
            if (page.TryGetProperty("pageprops", out var props) && props.TryGetProperty("disambiguation", out _)) continue;
            if (!page.TryGetProperty("langlinks", out var links) || links.GetArrayLength() == 0) continue;
            good.Add(page.GetProperty("title").GetString()!);
        }
        return map.Where(m => good.Contains(m.Value)).ToDictionary(m => m.Key, m => m.Value);
    }
}
