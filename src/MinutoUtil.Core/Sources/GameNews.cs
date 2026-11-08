using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MinutoUtil.Core.Cloud;
using MinutoUtil.Core.Media;

namespace MinutoUtil.Core.Sources;

/// <summary>
/// Official news about games for a news channel (config.json "news"), from sources that do not lag like Wikipedia:
/// the game's Steam store page as Brazil sees it (date, price in reais, Brazilian Portuguese audio and text), the
/// developer's announcements on Steam (betas, early access, patches) and the publishers' Portuguese blogs (PlayStation
/// Blog BR, Xbox Wire Brasil). <see cref="SourcesAsync"/> gives the writer its source texts; <see cref="SignalsAsync"/>
/// gives the daily idea list what is launching and selling. Every answer is untrusted data: sizes are capped, HTML is
/// reduced to plain text, and any source that fails is skipped with a warning rather than failing the run.
/// </summary>
public sealed partial class GameNews(HttpClient http, NewsOptions options, TimeProvider time)
{
    private const string Store = "https://store.steampowered.com";
    private const int MaxJsonBytes = 4_000_000;
    private const int MaxPostChars = 6000;
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    /// <summary>The news sources about one game, Portuguese first; empty when no official source knows it.</summary>
    public async Task<IReadOnlyList<SourceArticle>> SourcesAsync(string game, CancellationToken ct)
    {
        var sources = new List<SourceArticle>();
        var title = GameNames.Title(game);
        int? app = null;
        try
        {
            app = await SteamTrailers.FindAppAsync(http, game, ct);
        }
        catch (Exception ex) when (ex is MediaException or HttpRequestException or System.Text.Json.JsonException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"news: no Steam page for \"{title}\" ({TextUtil.Truncate(ex.Message, 120)})");
        }
        if (app is { } id)
        {
            if (await Try(() => StorePageAsync(id, ct), "Steam store page") is { } page) sources.Add(page);
            if (await Try(() => AnnouncementsAsync(id, title, ct), "Steam announcements") is { } news) sources.Add(news);
        }
        foreach (var feed in options.Feeds)
            if (await Try(() => FeedPostsAsync(feed, title, ct), feed.Name) is { } posts) sources.AddRange(posts);
        foreach (var s in sources) Log.Info($"news: {s.Title}, {s.Text.Length} characters");
        return sources;
    }

    /// <summary>
    /// Checks a news pick against the official sources, since a model's dates cannot be trusted (on 2026-11-06 a spare
    /// model gave Counter-Strike 2 and Warframe this week's dates because Steam lists them among popular new games):
    /// - on Steam, a launch takes Steam's own release date, which must be inside the window (the status follows it);
    ///   a beta or early access needs a recent Steam announcement or publisher post that names it;
    /// - off Steam (console exclusives), a recent publisher post must name the game and the claimed day ("19 de
    ///   novembro"), and for a beta or early access say so.
    /// Returns the checked date and status, or null when no official source backs the pick.
    /// </summary>
    public async Task<(string Date, string Status)?> ConfirmAsync(string article, DateOnly claimed, string status, CancellationToken ct)
    {
        var title = GameNames.Title(article);
        var phase = status is "beta" or "acesso antecipado";
        int? app = null;
        try
        {
            app = await SteamTrailers.FindAppAsync(http, article, ct);
        }
        catch (Exception ex) when (ex is MediaException or HttpRequestException or System.Text.Json.JsonException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
        }
        var posts = new List<Post>();
        foreach (var feed in options.Feeds)
            if (await Try(() => PostsAsync(feed, ct), feed.Name) is { } read)
                posts.AddRange(read.Where(p => p.Date >= Today.AddDays(-options.SourceDays) && (GameNames.Names(p.Title, title) || GameNames.Names(p.Body, title))));
        bool Says(string text) => !phase || PhaseWords().IsMatch(text);

        if (app is { } id)
        {
            if (phase)
            {
                var announced = await Try(() => AnnouncementsAsync(id, title, ct), "Steam announcements");
                return announced is not null && Says(announced.Text) || posts.Any(p => Says(p.Title + " " + p.Body))
                    ? (claimed.ToString("yyyy-MM-dd"), status) : null;
            }
            var data = await Try(async () => (await GetJsonAsync($"{Store}/api/appdetails?appids={id}&cc=br&l=english", ct))?[id.ToString(CultureInfo.InvariantCulture)]?["data"], "Steam store page");
            if (ParseSteamDate(data?["release_date"]?["date"]?.GetValue<string>()) is { } steam)
                return InWindow(steam) ? (steam.ToString("yyyy-MM-dd"), steam <= Today ? "lançado" : "lançamento") : null;
            // Steam shows no day yet ("Coming soon"): only a publisher post with the day backs it.
        }
        var day = claimed.ToString("d 'de' MMMM", Portuguese);
        return InWindow(claimed) && posts.Any(p => (p.Title + " " + p.Body).Contains(day, StringComparison.OrdinalIgnoreCase) && Says(p.Title + " " + p.Body))
            ? (claimed.ToString("yyyy-MM-dd"), status) : null;
    }

    private bool InWindow(DateOnly date) => date >= Today.AddDays(-options.RecentDays) && date <= Today.AddDays(options.AheadDays);

    /// <summary>
    /// What is new in games today, one line each, for the idea list: from Steam Brasil's most played new games, most
    /// awaited upcoming games and top sellers, only those whose Steam release date is inside the window, each with that
    /// date (Steam's "popular new" list is full of games from years ago, which a model then gave this week's dates); and
    /// the publishers' latest posts.
    /// </summary>
    public async Task<IReadOnlyList<string>> SignalsAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var (filter, label) in new[]
                 {
                     ("popularnew", "Steam Brasil, lançamentos recentes mais jogados"), ("topsellers", "Steam Brasil, mais vendidos"),
                     ("popularcomingsoon", "Steam Brasil, próximos lançamentos mais aguardados"),
                 })
        {
            var games = await Try(async () =>
            {
                using var response = await http.GetAsync($"{Store}/search/results/?filter={filter}&cc=br&l=english&infinite=1&count=50", HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var rows = JsonNode.Parse(await SafeHttp.ReadLimitedAsync(response, MaxJsonBytes, ct))?["results_html"]?.GetValue<string>() ?? "";
                return SearchRow().Matches(rows)
                    .Select(m => (Name: Clean(WebUtility.HtmlDecode(m.Groups[1].Value), 80), Date: ParseSteamDate(Clean(m.Groups[2].Value, 40))))
                    .Where(g => g.Name is not null && g.Date is { } d && InWindow(d))
                    .Select(g => $"{g.Name} ({(g.Date <= Today ? "saiu" : "sai")} {g.Date:dd/MM/yyyy})").Distinct().Take(25).ToList();
            }, label);
            if (games is { Count: > 0 }) lines.Add($"{label}: {string.Join("; ", games)}");
        }
        foreach (var feed in options.Feeds)
        {
            var posts = await Try(async () =>
            {
                var cutoff = Today.AddDays(-7);
                return (await PostsAsync(feed, ct)).Where(p => p.Date >= cutoff).Take(15)
                    .Select(p => $"{feed.Name}, {p.Date:dd/MM}: {p.Title}").ToList();
            }, feed.Name);
            if (posts is not null) lines.AddRange(posts);
        }
        return lines;
    }

    /// <summary>The store page's facts in Portuguese, from the English data (dates and languages in a fixed form).</summary>
    internal async Task<SourceArticle?> StorePageAsync(int app, CancellationToken ct)
    {
        var key = app.ToString(CultureInfo.InvariantCulture);
        var data = (await GetJsonAsync($"{Store}/api/appdetails?appids={key}&cc=br&l=english", ct))?[key]?["data"] as JsonObject;
        if (data is null) return null;
        var local = (await GetJsonAsync($"{Store}/api/appdetails?appids={key}&cc=br&l=brazilian", ct))?[key]?["data"] as JsonObject;
        var name = Clean(data["name"]?.GetValue<string>(), 120) ?? key;

        var text = new StringBuilder();
        text.AppendLine($"{name} na loja Steam do Brasil.");
        var release = data["release_date"];
        var soon = release?["coming_soon"]?.GetValue<bool>() == true;
        var raw = Clean(release?["date"]?.GetValue<string>(), 60);
        if (ParseSteamDate(raw) is { } date)
            text.AppendLine(soon
                ? $"Lançamento previsto na Steam: {date.ToString("d 'de' MMMM 'de' yyyy", Portuguese)}."
                : $"Data de lançamento na Steam: {date.ToString("d 'de' MMMM 'de' yyyy", Portuguese)}.");
        else if (soon)
            text.AppendLine(raw is null ? "Na Steam, o jogo ainda não tem data de lançamento." : $"Na Steam, o lançamento aparece como: {raw}.");
        if (PriceLine(data, soon) is { Length: > 0 } priceLine) text.AppendLine(priceLine);
        var systems = new[] { ("windows", "Windows"), ("mac", "Mac"), ("linux", "Linux") }
            .Where(p => data["platforms"]?[p.Item1]?.GetValue<bool>() == true).Select(p => p.Item2).ToList();
        if (systems.Count > 0) text.AppendLine($"Sistemas na Steam: {string.Join(", ", systems)}.");
        text.AppendLine(PortugueseSupport(data["supported_languages"]?.GetValue<string>()));
        if (Names(data["developers"]) is { Length: > 0 } developers) text.AppendLine($"Desenvolvedora: {developers}.");
        if (Names(data["publishers"]) is { Length: > 0 } publishers) text.AppendLine($"Distribuidora: {publishers}.");
        if (Plain(local?["short_description"]?.GetValue<string>()) is { Length: > 0 } summary) text.AppendLine($"Descrição da loja: {summary}");
        if (Plain(local?["about_the_game"]?.GetValue<string>()) is { Length: > 0 } about)
            text.AppendLine($"Sobre o jogo, na loja: {TextUtil.Truncate(about, 3000)}");
        return new SourceArticle("pt", $"Steam: {name}", $"{Store}/app/{key}/", text.ToString().Trim(), 0);
    }

    /// <summary>The developer's own announcements on Steam from the last <see cref="NewsOptions.SourceDays"/> days.</summary>
    internal async Task<SourceArticle?> AnnouncementsAsync(int app, string title, CancellationToken ct)
    {
        var key = app.ToString(CultureInfo.InvariantCulture);
        var found = await GetJsonAsync($"https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={key}&count=8&maxlength=2000&feeds=steam_community_announcements", ct);
        var cutoff = Today.AddDays(-options.SourceDays);
        var text = new StringBuilder();
        foreach (var item in (found?["appnews"]?["newsitems"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(item["date"]?.GetValue<long>() ?? 0).UtcDateTime);
            if (date < cutoff) continue;
            var body = Plain(SteamMarkup().Replace(item["contents"]?.GetValue<string>() ?? "", " "));
            text.AppendLine($"Anúncio de {date.ToString("d 'de' MMMM 'de' yyyy", Portuguese)}: {Clean(item["title"]?.GetValue<string>(), 160)}.");
            if (body is { Length: > 0 }) text.AppendLine(body);
            text.AppendLine();
        }
        return text.Length == 0 ? null
            : new SourceArticle("en", $"Steam, anúncios oficiais: {title}", $"{Store}/news/app/{key}/", text.ToString().Trim(), 0);
    }

    /// <summary>A feed's recent posts that name the game, at most two, newest first.</summary>
    internal async Task<IReadOnlyList<SourceArticle>> FeedPostsAsync(NewsFeed feed, string title, CancellationToken ct)
    {
        var cutoff = Today.AddDays(-options.SourceDays);
        return (await PostsAsync(feed, ct))
            .Where(p => p.Date >= cutoff && (GameNames.Names(p.Title, title) || GameNames.Names(p.Body, title)))
            .Take(2)
            .Select(p => new SourceArticle("pt", $"{feed.Name}: {p.Title}", p.Link,
                $"{p.Title}. Publicado em {p.Date.ToString("d 'de' MMMM 'de' yyyy", Portuguese)}.\n{TextUtil.Truncate(p.Body, MaxPostChars)}", 0))
            .ToList();
    }

    internal sealed record Post(string Title, string Link, DateOnly Date, string Body);

    /// <summary>A feed's items with their full text (content:encoded, else the description), newest first.</summary>
    internal async Task<IReadOnlyList<Post>> PostsAsync(NewsFeed feed, CancellationToken ct)
    {
        if (!Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"Not an https feed: {feed.Url}");
        XNamespace content = "http://purl.org/rss/1.0/modules/content/";
        var posts = new List<Post>();
        foreach (var item in (await TrendSignals.ReadFeedAsync(http, uri, ct)).Descendants("item"))
        {
            var title = Clean(WebUtility.HtmlDecode(item.Element("title")?.Value ?? ""), 200);
            var link = item.Element("link")?.Value.Trim() ?? "";
            if (title is null || !Uri.TryCreate(link, UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps) continue;
            if (!DateTimeOffset.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published)) continue;
            var body = Plain(item.Element(content + "encoded")?.Value ?? item.Element("description")?.Value) ?? "";
            posts.Add(new Post(title, page.AbsoluteUri, DateOnly.FromDateTime(published.UtcDateTime), body));
        }
        return posts.OrderByDescending(p => p.Date).ToList();
    }

    /// <summary>Steam's English release date ("Oct 28, 2026", "28 Oct, 2026"); null for "Coming soon", "Q1 2027" or a bare year.</summary>
    internal static DateOnly? ParseSteamDate(string? text) =>
        text is not null && DateOnly.TryParseExact(text.Replace(".", ""), ["MMM d, yyyy", "d MMM, yyyy", "MMMM d, yyyy", "d MMMM, yyyy", "MMM d yyyy", "d MMM yyyy"],
            CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ? date : null;

    /// <summary>
    /// Brazilian Portuguese on the store page, said plainly. Steam marks the languages with full audio with an asterisk
    /// ("Portuguese - Brazil&lt;strong&gt;*&lt;/strong&gt;").
    /// </summary>
    /// <summary>
    /// The price as a player reads it: a free game said to be free to play (so the writer never puts "por gratuito"),
    /// and a discount kept apart from the normal price (Steam's final price is the discounted one, often a pre-order
    /// offer), so the normal price is the one the video gives as the price. Empty when Steam has no price.
    /// </summary>
    internal static string PriceLine(JsonObject data, bool soon)
    {
        if (data["is_free"]?.GetValue<bool>() == true)
            return "Preço na Steam Brasil: o jogo é gratuito, dá para baixar e jogar de graça.";
        var price = data["price_overview"];
        var final = Clean(price?["final_formatted"]?.GetValue<string>(), 40);
        if (final is null) return soon ? "Preço na Steam Brasil: ainda não informado." : "";
        var discount = price?["discount_percent"]?.GetValue<int>() ?? 0;
        var initial = Clean(price?["initial_formatted"]?.GetValue<string>(), 40);
        if (discount <= 0 || initial is null || initial == final) return $"Preço na Steam Brasil: {final}.";
        var when = soon ? "na pré-venda" : "por tempo limitado";
        return $"Preço normal na Steam Brasil: {initial}. Desconto de {discount}% {when}: sai por {final}.";
    }

    internal static string PortugueseSupport(string? languages)
    {
        var entries = (languages ?? "").Split("<br>")[0].Split(',').Select(l => l.Trim()).ToList();
        var brazil = entries.FirstOrDefault(l => l.StartsWith("Portuguese - Brazil", StringComparison.OrdinalIgnoreCase));
        if (brazil is null) return "Português do Brasil na Steam: o jogo não lista o idioma, então não tem legendas nem dublagem em português.";
        return brazil.Contains('*')
            ? "Português do Brasil na Steam: interface, legendas e áudio, com dublagem."
            : "Português do Brasil na Steam: interface e legendas, sem dublagem.";
    }

    private static string Names(JsonNode? list) =>
        string.Join(", ", (list as JsonArray ?? []).Select(n => Clean(n?.GetValue<string>(), 80)).OfType<string>());

    /// <summary>HTML or a feed's markup as plain text: tags dropped, entities decoded, white space collapsed.</summary>
    internal static string? Plain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Tags().Replace(Breaks().Replace(html, "\n"), " ");
        text = WebUtility.HtmlDecode(text);
        text = Spaces().Replace(text, " ");
        text = string.Join("\n", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return text.Length == 0 ? null : SteamTrailers.Clean(text);
    }

    private static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = Spaces().Replace(SteamTrailers.Clean(text), " ").Trim();
        return clean.Length == 0 ? null : TextUtil.Truncate(clean, max);
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await SafeHttp.ReadLimitedAsync(response, MaxJsonBytes, ct));
    }

    /// <summary>One source at a time: a failure is a warning and the source is left out.</summary>
    private async Task<T?> Try<T>(Func<Task<T>> read, string what) where T : class?
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or XmlException or InvalidDataException
                                       or InvalidOperationException or TaskCanceledException)
        {
            Log.Warn($"news: {what} not read ({TextUtil.Truncate(ex.Message, 120)})");
            return null;
        }
    }

    /// <summary>Steam announcement markup: BBCode tags and image placeholders.</summary>
    [GeneratedRegex(@"\[/?[a-zA-Z0-9*]+(=[^\]]*)?\]|\{STEAM_CLAN_IMAGE\}\S*")]
    private static partial Regex SteamMarkup();

    [GeneratedRegex(@"<(br|/p|/h[1-6]|/li|/div)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    /// <summary>Words that say a post is about a beta or early access, in Portuguese or English.</summary>
    [GeneratedRegex(@"\b(beta|playtest|teste aberto|acesso antecipado|early access)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PhaseWords();

    /// <summary>One game of a Steam search page: its title and its release date as the store shows it.</summary>
    [GeneratedRegex("""<span class="title">(.*?)</span>.*?search_released[^>]*>\s*(.*?)\s*</div>""", RegexOptions.Singleline)]
    private static partial Regex SearchRow();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex Spaces();
}
