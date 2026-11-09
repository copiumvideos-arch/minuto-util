using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using MinutoUtil.Core;
using MinutoUtil.Core.Cloud;
using MinutoUtil.Core.Scripts;
using MinutoUtil.Core.Sources;

namespace MinutoUtil.Tests;

/// <summary>
/// Profissão Spoiler as a news channel: ideas are games just out or coming (checked against a date window), the queue
/// lives on the daily lists, and the writer reads the official news (Steam Brasil, publisher blogs). The network and
/// the models are replaced by fixed answers shaped like the real ones (checked live on 2026-11-06).
/// </summary>
public sealed class NewsTests
{
    private static readonly DateTimeOffset Today = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly NewsOptions News = new();

    private const string Search = """{"total":1,"items":[{"type":"app","name":"Phantom Blade Zero","id":4115450}]}""";

    private const string DetailsEnglish = """
        {"4115450":{"success":true,"data":{"name":"Phantom Blade Zero","is_free":false,
          "release_date":{"coming_soon":false,"date":"Oct 28, 2026"},
          "price_overview":{"currency":"BRL","final":22990,"final_formatted":"R$ 229,90"},
          "platforms":{"windows":true,"mac":false,"linux":false},
          "supported_languages":"English<strong>*</strong>, Portuguese - Brazil<strong>*</strong>, French<br><strong>*</strong>languages with full audio support",
          "developers":["S-GAME"],"publishers":["S-GAME"]}}}
        """;

    private const string DetailsPortuguese = """
        {"4115450":{"success":true,"data":{"name":"Phantom Blade Zero",
          "short_description":"Como Soul, você tem só sessenta e seis dias de vida.",
          "about_the_game":"<h2>Wuxia</h2><p>Combate rápido &amp; brutal.</p><img src=\"x.png\">"}}}
        """;

    private const string Announcements = """
        {"appnews":{"newsitems":[
          {"title":"Patch Notes 1.03","date":1793750400,"contents":"[h1]Fixes[/h1][list][*]Boss fight fixed[/list]{STEAM_CLAN_IMAGE}/abc.png","feedname":"steam_community_announcements"},
          {"title":"Pre-Orders Now Available","date":1786579200,"contents":"Old news","feedname":"steam_community_announcements"}
        ]}}
        """;

    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/"><channel>
          <item><title>Phantom Blade Zero chega ao PS5 com dublagem em português</title><link>https://blog.br.playstation.com/2026/11/03/pbz/</link>
            <pubDate>Tue, 03 Nov 2026 16:49:51 +0000</pubDate><content:encoded><![CDATA[<p>O jogo sai dia <b>28 de outubro</b> &amp; custa R$ 299,90.</p>]]></content:encoded></item>
          <item><title>Ofertas da Black Friday</title><link>https://blog.br.playstation.com/2026/11/03/bf/</link>
            <pubDate>Tue, 03 Nov 2026 12:42:18 +0000</pubDate><description>Descontos.</description></item>
          <item><title>Phantom Blade Zero: pré-venda aberta</title><link>https://blog.br.playstation.com/2026/08/13/pre/</link>
            <pubDate>Thu, 13 Aug 2026 12:00:00 +0000</pubDate><description>Antiga.</description></item>
          <item><title>Phantom Blade Zero em http</title><link>http://blog.br.playstation.com/x/</link>
            <pubDate>Wed, 04 Nov 2026 12:00:00 +0000</pubDate><description>Sem https.</description></item>
        </channel></rss>
        """;

    private static FakeHandler Web(bool steamDown = false) => new((request, _) =>
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (steamDown && url.Contains("steampowered.com", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (url.Contains("/api/storesearch/", StringComparison.Ordinal)) return FakeHandler.Json(HttpStatusCode.OK, Search);
        if (url.Contains("/api/appdetails", StringComparison.Ordinal))
            return FakeHandler.Json(HttpStatusCode.OK, url.Contains("l=english", StringComparison.Ordinal) ? DetailsEnglish : DetailsPortuguese);
        if (url.Contains("GetNewsForApp", StringComparison.Ordinal)) return FakeHandler.Json(HttpStatusCode.OK, Announcements);
        if (url.Contains("/search/results/", StringComparison.Ordinal))
            return FakeHandler.Json(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new
            {
                results_html = url.Contains("popularcomingsoon", StringComparison.Ordinal)
                    ? Row("DreamWorks Shrek 2 Remastered", "10 Nov, 2026") + Row("Far Future", "1 Jun, 2027") + Row("Just Volleyball", "November 2026")
                    : Row("Counter-Strike 2", "21 Aug, 2012") + Row("Phantom Blade Zero", "28 Oct, 2026") + Row("Call of Duty&reg;: Modern Warfare&reg; 4", "22 Oct, 2026"),
            }));
        if (url == "https://blog.br.playstation.com/feed/")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Feed, Encoding.UTF8, "application/rss+xml") };
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    /// <summary>One game as a Steam search page lists it (filter=...&amp;infinite=1).</summary>
    private static string Row(string title, string released) =>
        $"<a href=\"https://store.steampowered.com/app/1/\"><div class=\"responsive_search_name_combined\"><div class=\"col search_name ellipsis\"><span class=\"title\">{title}</span></div>" +
        $"<div class=\"col search_released responsive_secondrow\">\n {released} </div></div></a>";

    private static GameNews Reader(FakeHandler web) =>
        new(new HttpClient(web), News with { Feeds = [new("PlayStation Blog BR", "https://blog.br.playstation.com/feed/")] }, new FakeTimeProvider(Today));

    [Fact]
    public void Profissao_Spoiler_tells_curiosities_of_famous_games_from_a_backlog_the_cloud_finds_trailers_for()
    {
        // The news format (2026-11-06 to 2026-11-09) is off: the owner wants facts and curiosities about the games
        // everyone plays, explained the way Minuto Útil explains history. The news code stays for a channel that sets "news".
        var spoiler = Channel.Load(TestPaths.RepositoryRoot, "profissao-spoiler");

        Assert.Null(spoiler.Config.News);
        Assert.Null(spoiler.Backlog().News);
        Assert.True(spoiler.Backlog().Topics.Count >= 30);
        Assert.All(spoiler.Backlog().Topics, t => Assert.Equal("Curiosidades de games", t.Topic));
        Assert.Equal(spoiler.Backlog().Topics.Count, spoiler.Backlog().Topics.Select(t => t.Article).Distinct().Count());
        Assert.True(spoiler.Config.Writer.NoRepeats);
        Assert.False(Channel.Load(TestPaths.RepositoryRoot, "minuto-util").Config.Writer.NoRepeats);
        var writer = new ScriptWriter(new MinutoUtil.Core.Llm.ChainedLlmClient([]), spoiler.WriterPrompt(spoiler.Config.Format), spoiler.Config.Writer,
            "Profissão Spoiler");
        var prompt = writer.SystemPrompt();
        Assert.Contains("## How the explanation moves", prompt);
        Assert.Contains("End on the fact that answers the opening question", prompt);
        Assert.DoesNotContain("today's date", prompt);
        Assert.DoesNotMatch(@"\{[A-Z_]+\}", prompt);
        // Minuto Útil stays a channel of past events.
        Assert.Null(Channel.Load(TestPaths.RepositoryRoot, "minuto-util").Config.News);
    }

    [Fact]
    public async Task The_steam_page_gives_date_price_platforms_and_brazilian_portuguese_in_portuguese()
    {
        var page = await Reader(Web()).StorePageAsync(4115450, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal("Steam: Phantom Blade Zero", page.Title);
        Assert.Equal("https://store.steampowered.com/app/4115450/", page.Url);
        Assert.Contains("Data de lançamento na Steam: 28 de outubro de 2026.", page.Text);
        Assert.Contains("Preço na Steam Brasil: R$ 229,90.", page.Text);
        Assert.Contains("Sistemas na Steam: Windows.", page.Text);
        Assert.Contains("Português do Brasil na Steam: interface, legendas e áudio, com dublagem.", page.Text);
        Assert.Contains("Desenvolvedora: S-GAME.", page.Text);
        Assert.Contains("Descrição da loja: Como Soul, você tem só sessenta e seis dias de vida.", page.Text);
        // The store's HTML is reduced to text.
        Assert.Contains("Combate rápido & brutal.", page.Text);
        Assert.DoesNotContain("<", page.Text);
    }

    [Theory]
    [InlineData("English, Portuguese - Brazil<strong>*</strong><br><strong>*</strong>languages with full audio support", "com dublagem")]
    [InlineData("English<strong>*</strong>, Portuguese - Brazil, Spanish - Spain", "sem dublagem")]
    [InlineData("English, Portuguese - Portugal", "não lista o idioma")]
    [InlineData(null, "não lista o idioma")]
    public void Brazilian_portuguese_is_read_from_the_language_list(string? languages, string expected) =>
        Assert.Contains(expected, GameNews.PortugueseSupport(languages));

    [Theory]
    // A free game is said to be free, never as a price ("chegou ao PC por gratuito").
    [InlineData("""{"is_free":true}""", false, "Preço na Steam Brasil: o jogo é gratuito, dá para baixar e jogar de graça.")]
    [InlineData("""{"is_free":false,"price_overview":{"discount_percent":0,"initial_formatted":"","final_formatted":"R$ 229,90"}}""", false,
        "Preço na Steam Brasil: R$ 229,90.")]
    // Steam's final price is the discounted one: the normal price comes first, the offer apart (Shrek 2 Remastered, 7/11/2026).
    [InlineData("""{"is_free":false,"price_overview":{"discount_percent":10,"initial_formatted":"R$ 96,99","final_formatted":"R$ 87,29"}}""", true,
        "Preço normal na Steam Brasil: R$ 96,99. Desconto de 10% na pré-venda: sai por R$ 87,29.")]
    [InlineData("""{"is_free":false,"price_overview":{"discount_percent":25,"initial_formatted":"R$ 100,00","final_formatted":"R$ 75,00"}}""", false,
        "Preço normal na Steam Brasil: R$ 100,00. Desconto de 25% por tempo limitado: sai por R$ 75,00.")]
    [InlineData("""{"is_free":false}""", true, "Preço na Steam Brasil: ainda não informado.")]
    [InlineData("""{"is_free":false}""", false, "")]
    public void The_price_line_says_free_games_and_discounts_the_way_a_player_reads_them(string json, bool soon, string expected) =>
        Assert.Equal(expected, GameNews.PriceLine((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!, soon));

    [Theory]
    [InlineData("Oct 28, 2026", "2026-10-28")]
    [InlineData("28 Oct, 2026", "2026-10-28")]
    [InlineData("Nov 5, 2026", "2026-11-05")]
    [InlineData("Coming soon", null)]
    [InlineData("Q1 2027", null)]
    [InlineData("2027", null)]
    public void Only_a_real_day_counts_as_a_steam_date(string text, string? expected) =>
        Assert.Equal(expected, GameNews.ParseSteamDate(text)?.ToString("yyyy-MM-dd"));

    [Fact]
    public async Task Only_recent_https_posts_that_name_the_game_reach_the_writer()
    {
        var posts = await Reader(Web()).FeedPostsAsync(new("PlayStation Blog BR", "https://blog.br.playstation.com/feed/"), "Phantom Blade Zero", CancellationToken.None);

        var post = Assert.Single(posts);
        Assert.Equal("PlayStation Blog BR: Phantom Blade Zero chega ao PS5 com dublagem em português", post.Title);
        Assert.Equal("https://blog.br.playstation.com/2026/11/03/pbz/", post.Url);
        Assert.Contains("Publicado em 3 de novembro de 2026.", post.Text);
        Assert.Contains("O jogo sai dia 28 de outubro & custa R$ 299,90.", post.Text);
    }

    [Fact]
    public async Task Steam_announcements_keep_recent_ones_without_their_markup()
    {
        var news = await Reader(Web()).AnnouncementsAsync(4115450, "Phantom Blade Zero", CancellationToken.None);

        Assert.NotNull(news);
        Assert.Contains("Patch Notes 1.03", news.Text);
        Assert.Contains("Boss fight fixed", news.Text);
        Assert.DoesNotContain("[", news.Text);
        Assert.DoesNotContain("STEAM_CLAN_IMAGE", news.Text);
        // Older than news.sourceDays (45): August's pre-order post is left out.
        Assert.DoesNotContain("Pre-Orders", news.Text);
    }

    [Fact]
    public async Task All_sources_come_in_order_and_a_steam_outage_leaves_the_rest()
    {
        var all = await Reader(Web()).SourcesAsync("Phantom Blade Zero", CancellationToken.None);
        Assert.Equal(["Steam: Phantom Blade Zero", "Steam, anúncios oficiais: Phantom Blade Zero",
            "PlayStation Blog BR: Phantom Blade Zero chega ao PS5 com dublagem em português"], all.Select(s => s.Title));

        var withoutSteam = await Reader(Web(steamDown: true)).SourcesAsync("Phantom Blade Zero", CancellationToken.None);
        Assert.Equal("PlayStation Blog BR: Phantom Blade Zero chega ao PS5 com dublagem em português", Assert.Single(withoutSteam).Title);
    }

    [Fact]
    public async Task The_idea_list_gets_what_is_launching_selling_and_posted_this_week()
    {
        var lines = await Reader(Web()).SignalsAsync(CancellationToken.None);

        // Only games whose Steam date is inside the window, with that date: Counter-Strike 2 (2012), a game three weeks
        // out (Call of Duty, 22 Oct), one months ahead and one with no day are left out.
        Assert.Contains("Steam Brasil, lançamentos recentes mais jogados: Phantom Blade Zero (saiu 28/10/2026)", lines);
        Assert.Contains("Steam Brasil, próximos lançamentos mais aguardados: DreamWorks Shrek 2 Remastered (sai 10/11/2026)", lines);
        Assert.DoesNotContain(lines, l => l.Contains("Counter-Strike", StringComparison.Ordinal) || l.Contains("Call of Duty", StringComparison.Ordinal)
                                          || l.Contains("Far Future", StringComparison.Ordinal) || l.Contains("Volleyball", StringComparison.Ordinal));
        Assert.Contains("PlayStation Blog BR, 03/11: Phantom Blade Zero chega ao PS5 com dublagem em português", lines);
        // Older than a week: not news for the list.
        Assert.DoesNotContain(lines, l => l.Contains("pré-venda", StringComparison.Ordinal));
    }

    /// <summary>A model that answers with fixed ideas and keeps the prompts it got.</summary>
    private sealed class FakeLlm(params TrendIdea[] ideas) : MinutoUtil.Core.Llm.ILlmClient
    {
        public string Name => "fake";
        public List<string> Prompts { get; } = [];

        public Task<T> CompleteJsonAsync<T>(string system, string user, CancellationToken ct)
        {
            Prompts.Add(system + "\n" + user);
            return Task.FromResult((T)(object)new TrendIdeas(ideas));
        }
    }

    /// <summary>
    /// Wikipedia knows every asked title (no Portuguese twin). Steam knows Phantom Blade Zero (out 28 Oct), Counter-Strike 2
    /// (out in 2023), Shrek 2 Remastered (10 Nov) and Arc Raiders (an open beta announced); PlayStation Blog BR has a post on
    /// Ghost Game, a console exclusive, launching on 19 Nov. Nothing knows The Elder Scrolls VI.
    /// </summary>
    private static FakeHandler PickerWeb() => new((request, _) =>
    {
        var uri = request.RequestUri!;
        var url = uri.AbsoluteUri;
        if (uri.Host.EndsWith("wikipedia.org", StringComparison.Ordinal))
        {
            var titles = Uri.UnescapeDataString(System.Web.HttpUtility.ParseQueryString(uri.Query)["titles"] ?? "").Split('|');
            var pages = string.Join(",", titles.Select(t => "{\"title\":" + System.Text.Json.JsonSerializer.Serialize(t) + "}"));
            return FakeHandler.Json(HttpStatusCode.OK, "{\"query\":{\"pages\":[" + pages + "]}}");
        }
        (string Name, int Id, string Date)[] steam =
            [("Phantom Blade Zero", 1, "Oct 28, 2026"), ("Counter-Strike 2", 2, "Sep 27, 2023"), ("Shrek 2 Remastered", 3, "Nov 10, 2026"), ("Arc Raiders", 4, "Coming soon")];
        if (url.Contains("/api/storesearch/", StringComparison.Ordinal))
        {
            var term = System.Web.HttpUtility.ParseQueryString(uri.Query)["term"];
            var hit = steam.Where(g => g.Name == term).Select(g => "{\"type\":\"app\",\"id\":" + g.Id + ",\"name\":\"" + g.Name + "\"}");
            return FakeHandler.Json(HttpStatusCode.OK, "{\"items\":[" + string.Join(",", hit) + "]}");
        }
        if (url.Contains("/api/appdetails", StringComparison.Ordinal))
        {
            var id = int.Parse(System.Web.HttpUtility.ParseQueryString(uri.Query)["appids"]!);
            var game = steam.Single(g => g.Id == id);
            return FakeHandler.Json(HttpStatusCode.OK, "{\"" + id + "\":{\"success\":true,\"data\":{\"name\":\"" + game.Name + "\",\"release_date\":{\"coming_soon\":"
                + (game.Date == "Coming soon" ? "true" : "false") + ",\"date\":\"" + game.Date + "\"}}}}");
        }
        if (url.Contains("GetNewsForApp", StringComparison.Ordinal))
            return FakeHandler.Json(HttpStatusCode.OK, url.Contains("appid=4&", StringComparison.Ordinal)
                ? """{"appnews":{"newsitems":[{"title":"Open Beta starts November 14","date":1793750400,"contents":"Join the beta."}]}}"""
                : """{"appnews":{"newsitems":[]}}""");
        if (url == "https://blog.br.playstation.com/feed/")
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                <?xml version="1.0" encoding="UTF-8"?><rss version="2.0"><channel>
                  <item><title>Evercold chega ao PS5 em 23 de fevereiro</title><link>https://blog.br.playstation.com/evercold/</link>
                    <pubDate>Wed, 04 Nov 2026 12:00:00 +0000</pubDate><description>Expansão.</description></item>
                  <item><title>Ghost Game chega ao PS5 em 19 de novembro</title><link>https://blog.br.playstation.com/ghost/</link>
                    <pubDate>Wed, 04 Nov 2026 12:00:00 +0000</pubDate><description>Exclusivo.</description></item>
                </channel></rss>
                """, Encoding.UTF8, "application/rss+xml") };
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    [Fact]
    public async Task A_news_pick_is_kept_only_when_an_official_source_backs_its_date()
    {
        var llm = new FakeLlm(
            // Steam's own date wins over the model's.
            new("Phantom Blade Zero", "", 2026, "Chegou agora", "Steam", "2026-11-02", "lançado"),
            new("Shrek 2 Remastered", "", 2026, "Lançamentos dos games", "Steam", "2026-11-10", "Lançamento"),
            new("Arc Raiders", "", 2026, "Beta aberto", "Steam", "2026-11-14", "beta"),
            new("Ghost Game", "", 2026, "Lançamentos dos games", "PlayStation Blog", "2026-11-19", "lançamento"),
            // Next year, still inside the window: the news window decides, not the history channels' year rule.
            new("Evercold", "", 2027, "Lançamentos dos games", "PlayStation Blog", "2027-02-23", "lançamento"),
            // The two real failures of 2026-11-06: an old game given this week's date, and a famous game with no release.
            new("Counter-Strike 2", "", 2026, "Chegou agora", "Steam mais jogados", "2026-11-03", "lançado"),
            new("The Elder Scrolls VI: Sentinel", "", 2026, "Lançamentos dos games", "x", "2026-12-01", "lançamento"),
            // Outside the window or the rules before any source is asked.
            new("Old Game", "", 2026, "Chegou agora", "x", "2026-10-01", "lançado"),
            new("Future Game", "", 2027, "Lançamentos dos games", "x", "2027-06-01", "lançamento"),
            new("Wrong Way", "", 2026, "Lançamentos dos games", "x", "2026-11-01", "lançamento"),
            new("No Date", "", 2026, "Lançamentos dos games", "x", "", "lançamento"),
            new("Rumor Game", "", 2026, "Lançamentos dos games", "x", "2026-11-20", "rumor"));
        var picker = new TrendPicker(llm, new HttpClient(PickerWeb()), File.ReadAllText(Channel.Load(TestPaths.RepositoryRoot, "profissao-spoiler").TrendsPromptFile),
            "Profissão Spoiler", new FakeTimeProvider(Today), minYearsAgo: 0, news: News);
        var signals = new TrendSignals([], [], [], ["Steam Brasil, mais vendidos: Phantom Blade Zero (R$ 229,90)"]);

        var picks = await picker.PickAsync(signals, [], CancellationToken.None);

        Assert.Equal(["Phantom Blade Zero", "Shrek 2 Remastered", "Arc Raiders", "Ghost Game", "Evercold"], picks.Select(p => p.Article));
        Assert.Equal(("2026-10-28", "lançado"), (picks[0].Date, picks[0].Status));
        Assert.Equal(("2026-11-10", "lançamento"), (picks[1].Date, picks[1].Status));
        Assert.Equal(("2026-11-14", "beta"), (picks[2].Date, picks[2].Status));
        Assert.Equal(("2026-11-19", "lançamento"), (picks[3].Date, picks[3].Status));
        var prompt = Assert.Single(llm.Prompts);
        Assert.Contains("Today is 2026-11-06.", prompt);
        Assert.Contains("Games news today (official: Steam Brasil store lists and the publishers' blogs, the strongest signal):\n- Steam Brasil, mais vendidos: Phantom Blade Zero (R$ 229,90)".ReplaceLineEndings(), prompt.ReplaceLineEndings());
        Assert.Contains("Grand Theft Auto VI is left out", prompt);
    }

    [Fact]
    public async Task The_owners_own_idea_keeps_only_the_date_window()
    {
        var llm = new FakeLlm(new TrendIdea("Ghost Game", "", 2026, "Lançamentos dos games", "x", "2026-11-25", "lançamento"));
        var picker = new TrendPicker(llm, new HttpClient(PickerWeb()), "{CHANNEL} {COUNT}", "Profissão Spoiler", new FakeTimeProvider(Today), 0, News);

        var pick = Assert.Single(await picker.PickForIdeaAsync("o novo Ghost Game", [], CancellationToken.None));

        Assert.Equal(("2026-11-25", "lançamento"), (pick.Date, pick.Status));
    }

    private static IdeaList List(string date, params BacklogTopic[] ideas) => new(date, Today, ideas);

    [Fact]
    public void The_queue_takes_the_recent_lists_by_itself_and_lets_old_news_go()
    {
        using var temp = new TempFolder();
        var state = new CloudState(temp.Path) { Time = new FakeTimeProvider(Today) };
        var backlog = new TopicBacklog([], News: News);
        state.SaveIdeas(List("20261106", new BacklogTopic("Phantom Blade Zero", 2026, "Chegou agora", null, null, "2026-10-28", "lançado"),
            new BacklogTopic("Arc Raiders", 2026, "Beta aberto", null, null, "2026-11-14", "beta")));
        state.SaveIdeas(List("20261105", new BacklogTopic("Shrek 2 Remastered", 2026, "Lançamentos", null, null, "2026-11-10", "lançamento")));
        // Two days ago is outside news.listDays (2): that list is stale.
        state.SaveIdeas(List("20261104", new BacklogTopic("Stale List Game", 2026, "Lançamentos", null, null, "2026-11-20", "lançamento")));
        // Tapped before the channel turned to news (no date) and a tapped launch now three weeks old: both leave the queue.
        state.SaveApproved(new ApprovedIdea("20261020-01", new("The Witcher 3: Wild Hunt", 2015, "Curiosidades de games"), false, Today.AddDays(-17)));
        state.SaveApproved(new ApprovedIdea("20261020-02", new("Old Launch", 2026, "Chegou agora", null, null, "2026-10-15", "lançado"), false, Today.AddDays(-17)));
        // A text idea the owner sent today goes first.
        state.SaveApproved(new ApprovedIdea("t-1", new("Hades II", 2026, "Chegou agora", null, null, "2026-11-01", "lançado"), true, Today));

        var queue = state.Queue(backlog);

        Assert.Equal(["Hades II", "Phantom Blade Zero", "Arc Raiders", "Shrek 2 Remastered"], queue.Select(e => e.Topic.Article));
        Assert.Equal([QueueOrigin.Text, QueueOrigin.List, QueueOrigin.List, QueueOrigin.List], queue.Select(e => e.Origin));

        // A made video uses its game up, and a removed list idea stays out.
        state.Save(new RunRecord { Tag = "20261106-1300", Article = "Hades II", Status = RunStatus.Published, UpdatedAt = Today });
        state.Remove(backlog, queue[2].Key);
        Assert.Equal(["Phantom Blade Zero", "Shrek 2 Remastered"], state.Queue(backlog).Select(e => e.Topic.Article));
        Assert.Equal("Phantom Blade Zero", state.NextTopic(backlog).Next!.Article);

        // A channel of past events keeps its old queue: no lists, no dates needed.
        var history = state.Queue(new TopicBacklog([new("Magna Carta", 1215, "Democracia")]));
        Assert.Contains(history, e => e.Topic.Article == "The Witcher 3: Wild Hunt");
        Assert.DoesNotContain(history, e => e.Origin == QueueOrigin.List);
    }

    [Fact]
    public void The_idea_list_message_shows_what_happens_and_when()
    {
        var (text, _) = CloudFlow.IdeaListMessage(List("20261106", new BacklogTopic("Shrek 2 Remastered", 2026, "Lançamentos dos games", "Steam", null, "2026-11-10", "lançamento")));

        Assert.Contains("1. Shrek 2 Remastered (lançamento 10/11) · Lançamentos dos games · por: Steam", text);
    }

    [Fact]
    public void The_writer_gets_today_and_the_description_names_every_source()
    {
        var sources = new List<SourceArticle>
        {
            new("pt", "Steam: Phantom Blade Zero", "https://store.steampowered.com/app/4115450/", "Preço na Steam Brasil: R$ 229,90.", 0),
            new("en", "Phantom Blade Zero", "https://en.wikipedia.org/wiki/Phantom_Blade_Zero", "Phantom Blade Zero is an action game.", 1),
        };

        var prompt = ScriptWriter.UserPrompt("Lançamentos dos games", sources, 10000, null, "2026-11-06");
        Assert.StartsWith("Today is 2026-11-06.", prompt);
        Assert.Contains("=== SOURCE TEXT (pt, Steam: Phantom Blade Zero) ===", prompt);
        Assert.Contains("=== SOURCE TEXT (en, Wikipedia: Phantom Blade Zero) ===", prompt);
        Assert.DoesNotContain("Today is", ScriptWriter.UserPrompt("x", sources, 10000, null));

        var script = new VideoScript("t", "h", "", "", [], "Chegou em 28/10/2026.", ["games"]);
        var metadata = Pipeline.Metadata(script, sources, [], 50, "smallest", "fake", Path.GetTempPath());
        Assert.Contains("Fontes: Steam: Phantom Blade Zero, https://store.steampowered.com/app/4115450/.", metadata.Description);
        Assert.Contains("Fonte: Wikipedia (Phantom Blade Zero, https://en.wikipedia.org/wiki/Phantom_Blade_Zero). Texto sob CC BY-SA 4.0.", metadata.Description);
    }
}
