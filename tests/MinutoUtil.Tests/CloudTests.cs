using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using MinutoUtil.Core;
using MinutoUtil.Core.Cloud;
using MinutoUtil.Core.Publishing;

namespace MinutoUtil.Tests;

public sealed class TopicQueueTests
{
    private static RunRecord Run(string tag, string article, RunStatus status) =>
        new() { Tag = tag, Article = article, Status = status, UpdatedAt = PublishData.Now };

    [Fact]
    public void The_real_backlog_is_valid_and_has_no_repeats()
    {
        var backlog = TopicBacklog.Load(Path.Combine(TestPaths.RepositoryRoot, "config", "topics.json"));

        Assert.True(backlog.Topics.Count >= 30);
        Assert.Equal(backlog.Topics.Count, backlog.Topics.Select(t => t.Article.ToLowerInvariant()).Distinct().Count());
        Assert.All(backlog.Topics, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Article));
            Assert.False(string.IsNullOrWhiteSpace(t.Topic));
            Assert.InRange(t.Year, TrendPicker.MinYear, 2025);
            Assert.NotEqual(0, t.Year);
        });
        // Brazil only for now, mixed over the colony, the Empire and the Republic, and never two in a row from one series.
        Assert.All(backlog.Topics, t => Assert.InRange(t.Year, 1494, 2001));
        Assert.True(backlog.Topics.Count(t => t.Year < 1822) >= 10);
        Assert.True(backlog.Topics.Count(t => t.Year is >= 1822 and < 1889) >= 10);
        Assert.True(backlog.Topics.Count(t => t.Year >= 1889) >= 10);
        Assert.All(backlog.Topics.Zip(backlog.Topics.Skip(1)), p => Assert.NotEqual(p.First.Topic, p.Second.Topic));
        // The first video (Normandy, posted by hand) is not queued again.
        Assert.DoesNotContain(backlog.Topics, t => t.Article == "Normandy landings");
    }

    [Fact]
    public void Used_topics_are_skipped_and_a_topic_gets_a_second_chance_after_one_failure()
    {
        using var temp = new TempFolder();
        var state = new CloudState(temp.Path);
        var backlog = new TopicBacklog([new("A", 1900, "x"), new("B", 1901, "x"), new("C", 1902, "x"), new("D", 1903, "x")]);

        Assert.Equal(("A", 4), (state.NextTopic(backlog).Next!.Article, state.NextTopic(backlog).Left));

        state.Save(Run("20261027-1200", "A", RunStatus.Rejected));
        state.Save(Run("20261027-1300", "B", RunStatus.Failed));
        Assert.Equal("B", state.NextTopic(backlog).Next!.Article);

        state.Save(Run("20261027-1400", "b", RunStatus.Failed));
        Assert.Equal("C", state.NextTopic(backlog).Next!.Article);

        state.Save(Run("20261027-1500", "C", RunStatus.Review));
        state.Save(Run("20261027-1600", "D", RunStatus.Published));
        Assert.Equal((null, 0), (state.NextTopic(backlog).Next, state.NextTopic(backlog).Left));
    }

    [Theory]
    [InlineData("20261027-1114", true)]
    [InlineData("20261027-1114; rm -rf /", false)]
    [InlineData("../20261027-1114", false)]
    [InlineData("2026-10-27", false)]
    [InlineData("", false)]
    public void Run_tags_from_buttons_and_inputs_are_checked(string tag, bool valid)
    {
        if (valid) Assert.Equal(tag, RunTag.Validate(tag));
        else Assert.Throws<ArgumentException>(() => RunTag.Validate(tag));
    }

    [Fact]
    public void A_tag_is_the_utc_start_time()
    {
        Assert.Equal("20261027-1800", RunTag.For(PublishData.Now));
    }
}

[Collection("environment")]
public sealed class SealedTokenStoreTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Starts_from_the_secret_then_keeps_a_rotated_token_encrypted_in_the_state_folder()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "tiktok-token.enc");
        var store = new SealedFileTokenStore(path, Key, "tiktok", "rft.from-secret");

        Assert.Equal("rft.from-secret", store.Load());
        Assert.False(File.Exists(path));

        store.Save("rft.rotated-value");

        Assert.DoesNotContain("rotated", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        // The next job starts with the same secret but reads the newer token from the file.
        Assert.Equal("rft.rotated-value", new SealedFileTokenStore(path, Key, "tiktok", "rft.from-secret").Load());
    }

    [Fact]
    public void A_file_the_key_cannot_open_falls_back_to_the_secret_without_printing_tokens()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "tiktok-token.enc");
        new SealedFileTokenStore(path, Key, "tiktok", null).Save("rft.old-value");
        var lines = new List<string>();
        var sink = Log.Sink;
        Log.Sink = line => { lock (lines) lines.Add(line); };
        try
        {
            var otherKey = new byte[32];
            Assert.Equal("rft.new-secret", new SealedFileTokenStore(path, otherKey, "tiktok", "rft.new-secret").Load());
            // Same key, other platform: the file is bound to the platform it was written for.
            Assert.Equal("rft.new-secret", new SealedFileTokenStore(path, Key, "youtube", "rft.new-secret").Load());
        }
        finally
        {
            Log.Sink = sink;
        }
        Assert.Contains(lines, l => l.Contains("WARN") && l.Contains(SealedFileTokenStore.KeyEnvVar));
        Assert.DoesNotContain(lines, l => l.Contains("rft."));
    }

    [Fact]
    public void On_the_runner_the_sealed_store_is_used()
    {
        using var temp = new TempFolder();
        var variable = $"MINUTO_TEST_TOKEN_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "rft.secret");
        Environment.SetEnvironmentVariable(SealedFileTokenStore.StateDirEnvVar, temp.Path);
        Environment.SetEnvironmentVariable(SealedFileTokenStore.KeyEnvVar, Convert.ToBase64String(Key));
        try
        {
            var store = Assert.IsType<SealedFileTokenStore>(TokenStores.For(variable, "%TEMP%/none.bin", "tiktok"));
            Assert.Equal(Path.Combine(temp.Path, "tiktok-token.enc"), store.Path);
            Assert.Equal("rft.secret", store.Load());

            Environment.SetEnvironmentVariable(SealedFileTokenStore.KeyEnvVar, "not-a-key");
            Assert.Throws<PublishAuthException>(() => TokenStores.For(variable, "%TEMP%/none.bin", "tiktok"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            Environment.SetEnvironmentVariable(SealedFileTokenStore.StateDirEnvVar, null);
            Environment.SetEnvironmentVariable(SealedFileTokenStore.KeyEnvVar, null);
        }
    }
}

public sealed class CloudFlowTests
{
    private const string Tag = "20261027-1800";

    /// <summary>A Telegram that records every message, its text and buttons.</summary>
    private sealed class Messages
    {
        public List<(string Text, IReadOnlyList<TelegramButton> Buttons)> Sent { get; } = [];

        public Task Send(string text, IReadOnlyList<TelegramButton> buttons, CancellationToken ct)
        {
            Sent.Add((text, buttons));
            return Task.CompletedTask;
        }
    }

    private static (CloudState State, string Folder) Setup(TempFolder temp)
    {
        var folder = Path.Combine(temp.Path, "run");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "metadata.json"), JsonSerializer.Serialize(PublishData.Metadata(), Json.Options));
        File.WriteAllBytes(Path.Combine(folder, "video.mp4"), new byte[1000]);
        var state = new CloudState(Path.Combine(temp.Path, "state"));
        state.Save(new RunRecord { Tag = Tag, Article = "Normandy landings", Year = 1944, Topic = "Segunda Guerra Mundial",
            Status = RunStatus.Review, Title = "Como a Previsão do Tempo Mudou o Dia D", UpdatedAt = PublishData.Now });
        return (state, folder);
    }

    private static Publisher Publisher(FakeYouTube youtube, TikTokTests.FakeTikTok tiktok, Messages messages) =>
        new(PublishData.YouTubeConfig, new TikTokOptions(),
            () => new YouTubeUploader(new HttpClient(youtube), _ => Task.FromResult("t"), TimeSpan.Zero),
            () => new TikTokUploader(new HttpClient(tiktok), _ => Task.FromResult("t"), TimeSpan.Zero, TimeSpan.Zero),
            new FakeTimeProvider(PublishData.Now), (text, ct) => messages.Send(text, [], ct));

    [Fact]
    public async Task An_approved_run_goes_to_both_platforms_once_and_a_second_tap_posts_nothing()
    {
        using var temp = new TempFolder();
        var (state, folder) = Setup(temp);
        var youtube = new FakeYouTube();
        var tiktok = new TikTokTests.FakeTikTok();
        var messages = new Messages();
        var flow = new CloudFlow(state, messages.Send, new FakeTimeProvider(PublishData.Now));

        Assert.Equal(0, await flow.PublishAsync(Tag, folder, Publisher(youtube, tiktok, messages), CancellationToken.None));

        var run = state.Find(Tag)!;
        Assert.Equal(RunStatus.Published, run.Status);
        Assert.Equal("https://youtube.com/shorts/vid123", run.YouTubeUrl);
        Assert.Equal("v_inbox_file~v2.123", run.TikTokPublishId);
        // The TikTok caption on its own, then the summary with the YouTube link.
        Assert.Contains(messages.Sent, m => m.Text == PublishCopy.TikTokCaption(PublishData.Metadata(), new TikTokOptions()));
        Assert.Contains("https://youtube.com/shorts/vid123", messages.Sent[^1].Text);

        var requests = youtube.Requests.Count + tiktok.Requests.Count;
        Assert.Equal(0, await flow.PublishAsync(Tag, folder, Publisher(youtube, tiktok, messages), CancellationToken.None));
        Assert.Equal(requests, youtube.Requests.Count + tiktok.Requests.Count);
        Assert.StartsWith("Já publicado", messages.Sent[^1].Text);
    }

    [Fact]
    public async Task A_refused_youtube_thumbnail_still_publishes_and_tells_the_owner()
    {
        using var temp = new TempFolder();
        var (state, folder) = Setup(temp);
        PublishData.Thumbnail(folder);
        var youtube = new FakeYouTube { ThumbnailStatus = HttpStatusCode.Forbidden, ThumbnailBody = """{"error":{"errors":[{"reason":"forbidden"}]}}""" };
        var messages = new Messages();
        var flow = new CloudFlow(state, messages.Send, new FakeTimeProvider(PublishData.Now));

        Assert.Equal(0, await flow.PublishAsync(Tag, folder, Publisher(youtube, new TikTokTests.FakeTikTok(), messages), CancellationToken.None));

        Assert.Equal(RunStatus.Published, state.Find(Tag)!.Status);
        Assert.Contains("A capa não foi para o YouTube", messages.Sent[^1].Text);
        Assert.Contains("verified phone number", messages.Sent[^1].Text);
        // The TikTok notice points at the cover sent with the review.
        Assert.Contains(messages.Sent, m => m.Text.Contains("Para a capa, em Capa deixe a seleção no começo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tiktok_check_names_the_signed_in_account_and_where_each_latest_draft_is()
    {
        using var temp = new TempFolder();
        var (state, _) = Setup(temp);
        state.Save(state.Find(Tag)! with { Status = RunStatus.Published, TikTokPublishId = "v_inbox_file~v2.123" });
        state.Save(new RunRecord { Tag = "20261027-2224", Article = "Assassination of Archduke Franz Ferdinand", Status = RunStatus.Published,
            Title = "O gesto de amor que iniciou a Primeira Guerra Mundial", TikTokPublishId = "v_inbox_file~v2.456", UpdatedAt = PublishData.Now.AddHours(4) });
        state.Save(new RunRecord { Tag = "20261027-1400", Article = "Never published", Status = RunStatus.Review, UpdatedAt = PublishData.Now });
        var messages = new Messages();
        var flow = new CloudFlow(state, messages.Send, new FakeTimeProvider(PublishData.Now));
        var store = new MemoryTokenStore();
        store.Save("rft.0");
        var auth = new TikTokAuth(new HttpClient(new TikTokTests.FakeTikTokAuth()), new TikTokClient("k", "s"), store, TimeProvider.System);
        var tiktok = new TikTokTests.FakeTikTok { Statuses = new(["SEND_TO_USER_INBOX", "FAILED"]), FailReason = "file_format_check_failed" };
        var uploader = new TikTokUploader(new HttpClient(tiktok), auth.GetAccessTokenAsync, TimeSpan.Zero, TimeSpan.Zero);

        Assert.Equal(0, await flow.TikTokCheckAsync(auth.CurrentAccountAsync, uploader.FetchStatusAsync, CancellationToken.None));

        var text = Assert.Single(messages.Sent).Text;
        Assert.Contains("Conta conectada: Minuto Útil", text);
        // Newest first, one status request per draft, and the run that never reached TikTok is left out.
        Assert.Equal(["v_inbox_file~v2.456", "v_inbox_file~v2.123"],
            tiktok.Requests.Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("publish_id").GetString()));
        var lines = text.Split('\n').Where(l => l.StartsWith("• ")).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("Primeira Guerra Mundial", lines[0]);
        Assert.Contains("esperando você postar", lines[0]);
        Assert.Contains("falhou: file_format_check_failed", lines[1]);
        Assert.DoesNotContain("Never published", text);
    }

    [Fact]
    public async Task A_failed_tiktok_step_offers_a_retry_that_does_not_post_youtube_again()
    {
        using var temp = new TempFolder();
        var (state, folder) = Setup(temp);
        var youtube = new FakeYouTube();
        var messages = new Messages();
        var flow = new CloudFlow(state, messages.Send, new FakeTimeProvider(PublishData.Now));

        var failing = new TikTokTests.FakeTikTok { InitError = "spam_risk_too_many_pending_share" };
        Assert.Equal(1, await flow.PublishAsync(Tag, folder, Publisher(youtube, failing, messages), CancellationToken.None));

        var run = state.Find(Tag)!;
        Assert.Equal(RunStatus.Review, run.Status);
        Assert.Equal("https://youtube.com/shorts/vid123", run.YouTubeUrl);
        var (text, buttons) = messages.Sent[^1];
        Assert.Contains("O YouTube já foi: https://youtube.com/shorts/vid123", text);
        Assert.Equal("pub:" + Tag, Assert.Single(buttons).Data);

        var youtubeRequests = youtube.Requests.Count;
        Assert.Equal(0, await flow.PublishAsync(Tag, folder, Publisher(youtube, new TikTokTests.FakeTikTok(), messages), CancellationToken.None));
        Assert.Equal(youtubeRequests, youtube.Requests.Count);
        Assert.Equal(RunStatus.Published, state.Find(Tag)!.Status);
    }

    [Fact]
    public void A_remake_takes_the_same_topic_and_retires_the_old_run_unless_it_was_published()
    {
        using var temp = new TempFolder();
        var (state, _) = Setup(temp);
        state.Save(state.Find(Tag)! with { PortugueseArticle = "Desembarques na Normandia", Trend = "sua ideia: dia D" });
        var flow = new CloudFlow(state, new Messages().Send, new FakeTimeProvider(PublishData.Now));

        var topic = flow.Remake(Tag);

        Assert.Equal(new BacklogTopic("Normandy landings", 1944, "Segunda Guerra Mundial", "sua ideia: dia D", "Desembarques na Normandia"), topic);
        Assert.Equal(RunStatus.Rejected, state.Find(Tag)!.Status);
        state.Save(state.Find(Tag)! with { Status = RunStatus.Published });
        Assert.Throws<PublishRefusedException>(() => flow.Remake(Tag));
    }

    [Fact]
    public async Task A_rejected_run_is_never_published_and_a_published_one_cannot_be_rejected()
    {
        using var temp = new TempFolder();
        var (state, folder) = Setup(temp);
        var messages = new Messages();
        var flow = new CloudFlow(state, messages.Send, new FakeTimeProvider(PublishData.Now));

        await flow.RejectAsync(Tag, CancellationToken.None);
        Assert.Equal(RunStatus.Rejected, state.Find(Tag)!.Status);
        await Assert.ThrowsAsync<PublishRefusedException>(() =>
            flow.PublishAsync(Tag, folder, Publisher(new FakeYouTube(), new TikTokTests.FakeTikTok(), messages), CancellationToken.None));

        state.Save(state.Find(Tag)! with { Status = RunStatus.Published });
        await Assert.ThrowsAsync<PublishRefusedException>(() => flow.RejectAsync(Tag, CancellationToken.None));
    }

    [Fact]
    public async Task A_second_failure_takes_the_topic_out_of_the_queue()
    {
        using var temp = new TempFolder();
        var (state, _) = Setup(temp);
        var messages = new Messages();
        var flow = new CloudFlow(state, messages.Send, new FakeTimeProvider(PublishData.Now));
        state.Save(new RunRecord { Tag = "20261026-1800", Article = "Normandy landings", Status = RunStatus.Failed, UpdatedAt = PublishData.Now });

        await flow.FailedAsync(Tag, "render step failed", "https://github.com/o/r/actions/runs/1", CancellationToken.None);

        Assert.Equal(RunStatus.Failed, state.Find(Tag)!.Status);
        Assert.Contains("saiu da fila", messages.Sent[^1].Text);
        Assert.Contains("https://github.com/o/r/actions/runs/1", messages.Sent[^1].Text);
    }

    [Fact]
    public void The_review_caption_lists_the_warnings_to_check_and_fits_telegram()
    {
        var run = new RunRecord { Tag = Tag, Article = "Normandy landings", Year = 1944, Topic = "Segunda Guerra Mundial",
            Status = RunStatus.Rendering, UpdatedAt = PublishData.Now };
        var log = new[]
        {
            "11:14:24 WARN gemini:gemini-3.8-flash returned 503: {",
            "11:15:12 WARN Whisper heard only 77% of section 5's words; listen to it before posting",
            "11:15:12 voice: smallest:thiago, 62.56s spoken",
        }.Concat(Enumerable.Range(0, 40).Select(i => $"11:16:{i:00} WARN shot {i}: not enough pictures, showing {new string('x', 200)} again"));

        var caption = CloudFlow.ReviewCaption(run, PublishData.Metadata(), log);

        Assert.StartsWith("🎬 Como a Previsão do Tempo Mudou o Dia D", caption);
        Assert.Contains("Segunda Guerra Mundial · Normandy landings (1944)", caption);
        Assert.Contains("Whisper heard only 77%", caption);
        Assert.DoesNotContain("gemini-3.8-flash", caption);
        Assert.Contains("mais 35 no run.log", caption);
        Assert.True(caption.Length <= TelegramNotifier.MaxCaption);
        Assert.Equal(["pub:" + Tag, "redo:" + Tag, "rej:" + Tag], CloudFlow.ReviewButtons(Tag).Select(b => b.Data));
    }
}

public sealed class TelegramVideoTests
{
    [Fact]
    public async Task The_review_video_goes_up_with_its_buttons_and_errors_hide_the_bot_token()
    {
        using var temp = new TempFolder();
        var video = Path.Combine(temp.Path, "video.mp4");
        File.WriteAllBytes(video, Encoding.ASCII.GetBytes("fake-mp4-bytes"));
        var telegram = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, """{"ok":true}"""));
        var notifier = new TelegramNotifier(new HttpClient(telegram), "123:secret", "42");

        await notifier.SendVideoAsync(video, "legenda", CloudFlow.ReviewButtons("20261027-1800"), CancellationToken.None);

        var (request, body) = Assert.Single(telegram.Requests);
        Assert.Equal("https://api.telegram.org/bot123:secret/sendVideo", request.RequestUri!.AbsoluteUri);
        Assert.Contains("fake-mp4-bytes", body);
        Assert.Contains("name=video; filename=video.mp4", body);
        Assert.Contains("\"callback_data\":\"pub:20261027-1800\"", body);
        Assert.Contains("\"callback_data\":\"rej:20261027-1800\"", body);

        var failing = new TelegramNotifier(new HttpClient(new FakeHandler((_, _) =>
            FakeHandler.Json(HttpStatusCode.RequestEntityTooLarge, """{"ok":false,"description":"Request Entity Too Large"}"""))), "123:secret", "42");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => failing.SendVideoAsync(video, "x", [], CancellationToken.None));
        Assert.Contains("Too Large", ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public async Task The_cover_goes_up_as_a_photo_with_how_to_use_it_on_tiktok()
    {
        using var temp = new TempFolder();
        var cover = Path.Combine(temp.Path, MinutoUtil.Core.Video.Thumbnail.FileName);
        File.WriteAllBytes(cover, Encoding.ASCII.GetBytes("fake-jpeg-bytes"));
        var telegram = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, """{"ok":true}"""));
        var notifier = new TelegramNotifier(new HttpClient(telegram), "123:secret", "42");

        await notifier.SendPhotoAsync(cover, CloudFlow.CoverCaption, CancellationToken.None);

        var (request, body) = Assert.Single(telegram.Requests);
        Assert.Equal("https://api.telegram.org/bot123:secret/sendPhoto", request.RequestUri!.AbsoluteUri);
        Assert.Contains("fake-jpeg-bytes", body);
        Assert.Contains("name=photo; filename=thumbnail.jpg", body);
        Assert.Contains("No TikTok ela é o primeiro quadro", body);
    }
}

public sealed class TrendTests
{
    /// <summary>A writer model that answers with fixed ideas and keeps the prompts it got.</summary>
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
    /// Wikipedia's query API as it answers for these titles. English: a redirect, a missing page, a disambiguation page,
    /// one without a Portuguese twin. Portuguese: one article the model named, one that does not exist.
    /// </summary>
    private static FakeHandler Wikipedia() => new((request, _) =>
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url.Contains("list=search", StringComparison.Ordinal))
            return url.StartsWith("https://pt.", StringComparison.Ordinal)
                ? FakeHandler.Json(HttpStatusCode.OK, """{"query":{"search":[{"title":"Urna eletrônica (Brasil)"},{"title":"Voto eletrônico"}]}}""")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (url.StartsWith("https://pt.wikipedia.org/w/api.php?", StringComparison.Ordinal))
            return FakeHandler.Json(HttpStatusCode.OK, """
                {"query":{"pages":[{"title":"Urna eletrônica (Brasil)"},{"title":"Urna Inventada","missing":true}]}}
                """);
        Assert.StartsWith("https://en.wikipedia.org/w/api.php?", url);
        return FakeHandler.Json(HttpStatusCode.OK, """
            {"query":{
              "normalized":[{"from":"magna carta","to":"Magna carta"}],
              "redirects":[{"from":"Magna carta","to":"Magna Carta"}],
              "pages":[
                {"title":"Magna Carta","langlinks":[{"lang":"pt","title":"Magna Carta"}]},
                {"title":"Proclamation of the Republic (Brazil)","langlinks":[{"lang":"pt","title":"Proclamação"}]},
                {"title":"Made Up Battle","missing":true},
                {"title":"Election","pageprops":{"disambiguation":""},"langlinks":[{"lang":"pt","title":"Eleição"}]},
                {"title":"Electronic voting in Brazil"},
                {"title":"Storming of the Bastille","langlinks":[{"lang":"pt","title":"Tomada da Bastilha"}]},
                {"title":"Battle of Marathon","langlinks":[{"lang":"pt","title":"Batalha de Maratona"}]}
              ]}}
            """);
    });

    private static TrendPicker Picker(FakeLlm llm, FakeHandler wikipedia) =>
        new(llm, new HttpClient(wikipedia), File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "prompts", "trends.md")),
            "Minuto Útil", new FakeTimeProvider(PublishData.Now));

    [Fact]
    public async Task Picks_are_checked_on_wikipedia_and_against_the_rules_before_they_are_offered()
    {
        var llm = new FakeLlm(
            new("magna carta", "", 1215, "Democracia", "eleições 2026"),
            new("Made Up Battle", "", 1900, "Guerras", "x"),
            new("Election", "", 1950, "Democracia", "x"),
            new("Electronic voting in Brazil", "Urna eletrônica (Brasil)", 1996, "Democracia", "eleições"),
            new("Storming of the Bastille", "", 1789, "Revoluções", "x"),
            new("2022 Brazilian general election", "", 2022, "Democracia", "eleições"),
            new("Battle of Marathon", "", -490, "Grécia Antiga", "democracia"),
            new("Battle of Salamis", "", 0, "Grécia Antiga", "x"),
            new("Storming of the Bastille", "", -5000, "Revoluções", "x"),
            new("Proclamation of the Republic (Brazil)", "Urna Inventada", 1889, "História do Brasil", "presidentes do Brasil"));
        var signals = new TrendSignals(["Xi Jinping parabeniza Lula por reeleição - G1"], ["eleições: Segundo turno movimenta o país"], ["Lista de presidentes do Brasil"]);

        var picks = await Picker(llm, Wikipedia()).PickAsync(signals, ["Storming of the Bastille"], CancellationToken.None);

        Assert.Equal(["Magna Carta", "Electronic voting in Brazil", "Battle of Marathon", "Proclamation of the Republic (Brazil)"], picks.Select(p => p.Article));
        Assert.Equal(-490, picks[2].Year);
        Assert.Equal(new BacklogTopic("Magna Carta", 1215, "Democracia", "eleições 2026", "Magna Carta"), picks[0]);
        // The Portuguese article the model named is used when it exists; a made-up one falls back to the language link.
        Assert.Equal("Urna eletrônica (Brasil)", picks[1].PortugueseArticle);
        Assert.Equal("Proclamação", picks[3].PortugueseArticle);
        var prompt = Assert.Single(llm.Prompts);
        Assert.Contains("Minuto Útil", prompt);
        Assert.Contains("Give 10 picks", prompt);
        Assert.Contains("Top news in Brazil today (Google News headlines, the strongest signal):\n- Xi Jinping parabeniza Lula por reeleição - G1".ReplaceLineEndings(), prompt.ReplaceLineEndings());
        Assert.Contains("- eleições: Segundo turno movimenta o país", prompt);
        Assert.Contains("skip those unless one has a real historical story", prompt);
        Assert.Contains("- Lista de presidentes do Brasil", prompt);
        Assert.Contains("Already made (do not pick):\n- Storming of the Bastille".ReplaceLineEndings(), prompt.ReplaceLineEndings());
        Assert.Contains("never a living politician", prompt);
        Assert.Contains("the channel tells Brazil's history only", prompt);
    }

    [Fact]
    public async Task An_owner_idea_becomes_the_history_that_answers_it()
    {
        var llm = new FakeLlm(new TrendIdea("Electronic voting in Brazil", "Urna eletrônica (Brasil)", 1996, "Democracia", "urna eletrônica"));

        var picks = await Picker(llm, Wikipedia()).PickForIdeaAsync("por que só o Brasil usa urna eletrônica?", [], CancellationToken.None);

        Assert.Equal(new BacklogTopic("Electronic voting in Brazil", 1996, "Democracia", "sua ideia: por que só o Brasil usa urna eletrônica?",
            "Urna eletrônica (Brasil)"), Assert.Single(picks));
        Assert.Contains("Idea: por que só o Brasil usa urna eletrônica?", llm.Prompts[0]);
        // The model chooses among real titles: Portuguese search results are in the prompt; a failed English search only warns.
        Assert.Contains("Portuguese Wikipedia search results:\n- Urna eletrônica (Brasil)\n- Voto eletrônico".ReplaceLineEndings(), llm.Prompts[0].ReplaceLineEndings());
        Assert.Contains("English Wikipedia search results:\n\n".ReplaceLineEndings(), llm.Prompts[0].ReplaceLineEndings());
        Assert.DoesNotContain("Searched in Brazil today", llm.Prompts[0]);
    }

    [Fact]
    public async Task Trend_sources_are_read_as_untrusted_data()
    {
        const string rss = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss xmlns:ht="https://trends.google.com/trending/rss" version="2.0"><channel>
              <item><title>eleições</title><ht:news_item><ht:news_item_title>Segundo turno &lt;b&gt;movimenta&lt;/b&gt; o país</ht:news_item_title></ht:news_item></item>
              <item><title>menino</title></item>
            </channel></rss>
            """;
        const string top = """
            {"items":[{"articles":[{"article":"Wikipédia:Página_principal"},{"article":"Lista_de_presidentes_do_Brasil"},{"article":"Especial:Pesquisar"},{"article":"Proclamação_da_República_do_Brasil"}]}]}
            """;
        const string news = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel>
              <item><title>Xi Jinping parabeniza Lula por reeleição - G1</title></item>
              <item><title>Israel vai às urnas em referendo - Estadão</title></item>
            </channel></rss>
            """;
        var handler = new FakeHandler((request, _) =>
            request.RequestUri == TrendSignals.GoogleTrendsFeed ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(rss) }
            : request.RequestUri == TrendSignals.GoogleNewsFeed ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(news) }
            : FakeHandler.Json(HttpStatusCode.OK, top));

        var signals = await TrendSignals.FetchAsync(new HttpClient(handler), new DateOnly(2026, 10, 26), CancellationToken.None);

        Assert.Equal(["Xi Jinping parabeniza Lula por reeleição - G1", "Israel vai às urnas em referendo - Estadão"], signals.Headlines);
        Assert.Equal(["eleições: Segundo turno  b movimenta /b  o país", "menino"], signals.Searches);
        Assert.Equal(["Lista de presidentes do Brasil", "Proclamação da República do Brasil"], signals.WikipediaReads);
        Assert.Equal("https://wikimedia.org/api/rest_v1/metrics/pageviews/top/pt.wikipedia/all-access/2026/10/26", handler.Requests[2].Request.RequestUri!.AbsoluteUri);

        // A feed with a DTD is refused (no entity expansion); Wikipedia still counts. A source that is down is skipped.
        var hostile = new FakeHandler((request, _) => request.RequestUri == TrendSignals.GoogleTrendsFeed
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY a \"aaaa\">]><rss><channel><item><title>&a;</title></item></channel></rss>") }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var none = await TrendSignals.FetchAsync(new HttpClient(hostile), new DateOnly(2026, 10, 26), CancellationToken.None);
        Assert.True(none.IsEmpty);
    }

    [Fact]
    public void Text_ideas_go_first_then_tapped_ideas_then_the_backlog()
    {
        using var temp = new TempFolder();
        var state = new CloudState(temp.Path);
        var backlog = new TopicBacklog([new("Sinking of the Titanic", 1912, "Grandes desastres")]);
        var list = new IdeaList("20261028", PublishData.Now, [new("Magna Carta", 1215, "Democracia", "eleições"), new("Lei Áurea", 1888, "História do Brasil", "eleições")]);
        state.SaveIdeas(list);
        Assert.Equal("Sinking of the Titanic", state.NextTopic(backlog).Next!.Article);

        state.SaveApproved(new ApprovedIdea("20261028-02", list.Ideas[1], FromText: false, PublishData.Now));
        state.SaveApproved(new ApprovedIdea("20261028-01", list.Ideas[0], FromText: false, PublishData.Now.AddMinutes(1)));
        Assert.Equal("Lei Áurea", state.NextTopic(backlog).Next!.Article);

        state.SaveApproved(new ApprovedIdea("20261028-120000-text", new("Electronic voting in Brazil", 1996, "Democracia", "sua ideia: urna", "Urna eletrônica (Brasil)"),
            FromText: true, PublishData.Now.AddHours(2)));
        var next = state.NextTopic(backlog).Next!;
        Assert.Equal(("Electronic voting in Brazil", "Urna eletrônica (Brasil)"), (next.Article, next.PortugueseArticle));

        state.Save(new RunRecord { Tag = "20261028-1300", Article = "Electronic voting in Brazil", Status = RunStatus.Review, UpdatedAt = PublishData.Now });
        state.Save(new RunRecord { Tag = "20261028-1400", Article = "Lei Áurea", Status = RunStatus.Published, UpdatedAt = PublishData.Now });
        Assert.Equal("Magna Carta", state.NextTopic(backlog).Next!.Article);
        Assert.Contains("Magna Carta", state.TakenArticles());
        Assert.Throws<ArgumentException>(() => state.SaveApproved(new ApprovedIdea("../x", list.Ideas[0], false, PublishData.Now)));
        Assert.Throws<ArgumentException>(() => state.Ideas("2026-10-28"));
    }

    [Fact]
    public void The_idea_list_is_numbered_with_a_button_per_idea()
    {
        var list = new IdeaList("20261028", PublishData.Now, Enumerable.Range(1, 10)
            .Select(i => new BacklogTopic($"Event {i}", 1900 + i, "Democracia", "eleições", i == 1 ? "Evento um" : null)).ToList());

        var (text, buttons) = CloudFlow.IdeaListMessage(list);

        Assert.Contains("\n1. Evento um (1901) · Democracia · por: eleições", text.ReplaceLineEndings("\n"));
        Assert.Contains("\n10. Event 10 (1910)", text.ReplaceLineEndings("\n"));
        Assert.Equal(10, buttons.Count);
        Assert.Equal(new TelegramButton("7", "idea:20261028:7"), buttons[6]);
    }

    [Fact]
    public void The_review_caption_says_what_inspired_the_video()
    {
        var run = new RunRecord { Tag = "20261027-1800", Article = "Magna Carta", Year = 1215, Topic = "Democracia", Trend = "eleições 2026",
            Status = RunStatus.Rendering, UpdatedAt = PublishData.Now };
        Assert.Contains("💡 Inspiração: eleições 2026", CloudFlow.ReviewCaption(run, PublishData.Metadata(), []));
    }
}

public sealed class CloudflareAiTests
{
    [Fact]
    public async Task The_spare_writer_calls_workers_ai_on_the_account_and_hands_over_to_the_next_model()
    {
        var handler = new FakeHandler((request, body) => body.Contains("\"@cf/openai/gpt-oss-120b\"")
            ? FakeHandler.Json(HttpStatusCode.TooManyRequests, """{"errors":[{"message":"you have used up your daily free allocation of 10,000 neurons"}]}""")
            : FakeHandler.Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"{\"ideas\":[]}"}}]}"""));
        var usage = new RunUsage(5);
        var client = MinutoUtil.Core.Llm.OpenRouterClient.ForCloudflare(new HttpClient(handler), "cf_ai_token", "b77e134015ef6359c129cf8cfe3bb38a",
            ["@cf/openai/gpt-oss-120b", "@cf/meta/llama-3.3-70b-instruct-fp8-fast"], usage);

        var answer = await client.CompleteJsonAsync<TrendIdeas>("system", "user", CancellationToken.None);

        Assert.Empty(answer.Ideas);
        Assert.Equal("cloudflare:@cf/openai/gpt-oss-120b", client.Name);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("https://api.cloudflare.com/client/v4/accounts/b77e134015ef6359c129cf8cfe3bb38a/ai/v1/chat/completions", r.Request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer cf_ai_token", r.Request.Headers.Authorization!.ToString());
            Assert.DoesNotContain("response_format", r.Body);
            Assert.Contains("JSON schema", r.Body);
        });
        Assert.Throws<MinutoUtil.Core.Llm.LlmException>(() =>
            MinutoUtil.Core.Llm.OpenRouterClient.ForCloudflare(new HttpClient(handler), "t", "../evil", ["m"], usage));
    }
}

public sealed class QueueTests
{
    private static readonly TopicBacklog Backlog = new([
        new("Sinking of the Titanic", 1912, "Grandes desastres"),
        new("Santos-Dumont 14-bis", 1906, "História do Brasil"),
        new("Attack on Pearl Harbor", 1941, "Segunda Guerra Mundial"),
        new("Christmas truce", 1914, "Primeira Guerra Mundial"),
    ]);

    private static CloudState WithIdeas(TempFolder temp)
    {
        var state = new CloudState(temp.Path);
        state.SaveApproved(new ApprovedIdea("20261028-01", new("Magna Carta", 1215, "Democracia", "eleições"), FromText: false, PublishData.Now));
        state.SaveApproved(new ApprovedIdea("20261028-120000-text", new("Electronic voting in Brazil", 1996, "Democracia", "sua ideia: urna",
            "Urna eletrônica (Brasil)"), FromText: true, PublishData.Now.AddHours(1)));
        return state;
    }

    private static string[] Articles(CloudState state) => [.. state.Queue(Backlog).Select(e => e.Topic.Article)];

    [Fact]
    public void The_queue_is_text_ideas_then_tapped_ideas_then_the_backlog_without_used_or_removed_topics()
    {
        using var temp = new TempFolder();
        var state = WithIdeas(temp);
        state.Save(new RunRecord { Tag = "20261028-1000", Article = "Sinking of the Titanic", Status = RunStatus.Published, UpdatedAt = PublishData.Now });

        Assert.Equal(["Electronic voting in Brazil", "Magna Carta", "Santos-Dumont 14-bis", "Attack on Pearl Harbor", "Christmas truce"], Articles(state));
        Assert.Equal("Electronic voting in Brazil", state.NextTopic(Backlog).Next!.Article);
        Assert.Equal(3, state.NextTopic(Backlog).Left);
    }

    [Fact]
    public void Moving_pins_the_order_and_removing_takes_an_item_out_for_good()
    {
        using var temp = new TempFolder();
        var state = WithIdeas(temp);

        // /mover 6 2: position 6 (Christmas truce) becomes second.
        state.Move(Backlog, "6", 2);
        Assert.Equal(["Electronic voting in Brazil", "Christmas truce", "Magna Carta", "Sinking of the Titanic", "Santos-Dumont 14-bis", "Attack on Pearl Harbor"], Articles(state));

        // The ⏫ button carries the key, which still finds the item after the move.
        var key = state.Queue(Backlog).Single(e => e.Topic.Article == "Attack on Pearl Harbor").Key;
        state.Move(Backlog, key, 1);
        Assert.Equal("Attack on Pearl Harbor", Articles(state)[0]);
        Assert.Equal("Christmas truce", Articles(state)[2]);

        // 🗑 on a text idea deletes its file; on a backlog topic it keeps it out of the queue too.
        state.Remove(Backlog, "a-20261028-120000-text");
        state.Remove(Backlog, state.Queue(Backlog).Single(e => e.Topic.Article == "Sinking of the Titanic").Key);
        Assert.DoesNotContain("Electronic voting in Brazil", Articles(state));
        Assert.DoesNotContain("Sinking of the Titanic", Articles(state));
        Assert.DoesNotContain(state.Approved(), a => a.Id == "20261028-120000-text");
        Assert.Equal(4, Articles(state).Length);

        Assert.Throws<ArgumentException>(() => state.Find(Backlog, "9"));
        Assert.Throws<ArgumentException>(() => state.Find(Backlog, "b-not-there"));
    }

    [Fact]
    public void Years_before_Christ_read_as_a_C_in_the_queue()
    {
        using var temp = new TempFolder();
        var state = new CloudState(temp.Path);
        var backlog = new TopicBacklog([new("Assassination of Julius Caesar", -44, "Roma Antiga"), new("Battle of Hastings", 1066, "Idade Média")]);

        var (text, _) = CloudFlow.QueueMessage(state.Queue(backlog), "Fila");

        Assert.Contains("1. Assassination of Julius Caesar (44 a.C.) · fila padrão", text);
        Assert.Contains("2. Battle of Hastings (1066) · fila padrão", text);
        Assert.Equal("?", TextUtil.YearLabel(null));
    }

    [Fact]
    public void The_queue_message_numbers_the_items_with_top_and_remove_buttons_by_key()
    {
        using var temp = new TempFolder();
        var state = WithIdeas(temp);

        var (text, buttons) = CloudFlow.QueueMessage(state.Queue(Backlog), "⏫ No topo: X");

        var lines = text.ReplaceLineEndings("\n");
        Assert.StartsWith("⏫ No topo: X", lines);
        Assert.Contains("\n1. Urna eletrônica (Brasil) (1996) · sua ideia", lines);
        Assert.Contains("\n2. Magna Carta (1215) · da lista do dia", lines);
        Assert.Contains("\n3. Sinking of the Titanic (1912) · fila padrão", lines);
        Assert.Equal(12, buttons.Count);
        Assert.Equal(new TelegramButton("⏫ 1", "qt:a-20261028-120000-text"), buttons[0]);
        Assert.Equal(new TelegramButton("🗑 3", "qd:b-sinking-of-the-titanic"), buttons[5]);
        Assert.All(buttons, b => Assert.True(System.Text.Encoding.UTF8.GetByteCount(b.Data) <= 64));
        Assert.Contains("A fila está vazia", CloudFlow.QueueMessage([]).Text);
    }
}

public sealed class BotFeatureTests
{
    private static readonly MinutoUtil.Core.Scripts.VideoScript Script = new("Como surgiu a urna", "Por que só o Brasil?", "1996", "Brasil",
        [
            new("Em mil novecentos e noventa e seis, o Brasil testou a urna.", ["voting machine 1996"], ["urna 1996"], "q1"),
            new("Ela não se liga à internet.", ["voting machine offline"], ["urna offline"], "q2"),
        ], "A urna chegou em 1996.", ["urna"]) { Question = "Você prefere urna ou papel?" };

    private static readonly RunRecord Run = new() { Tag = "20261028-1000", Article = "Electronic voting in Brazil", Year = 1996, Topic = "Democracia",
        Status = RunStatus.Script, UpdatedAt = PublishData.Now };

    [Fact]
    public void The_script_message_has_labeled_lines_and_the_run_tag_the_relay_reads()
    {
        var text = ScriptReview.Message(Run, Script).ReplaceLineEndings("\n");

        Assert.StartsWith(ScriptReview.Marker, text);
        Assert.Contains("\nTítulo: Como surgiu a urna\nGancho: Por que só o Brasil?\n1. Em mil novecentos", text);
        Assert.Contains("\n2. Ela não se liga à internet.\nPergunta: Você prefere urna ou papel?", text);
        Assert.EndsWith("Execução 20261028-1000", text);
        Assert.Equal(["sok:20261028-1000", "snew:20261028-1000", "rej:20261028-1000"], ScriptReview.Buttons(Run.Tag).Select(b => b.Data));
    }

    [Fact]
    public void The_owners_reply_replaces_only_the_lines_it_carries_and_keeps_the_picture_searches()
    {
        var (edited, changed) = ScriptReview.Apply(Script, "título: A urna que mudou o Brasil\r\n2) Ela funciona sem internet, de propósito.\nobrigado!\n7. linha que não existe\nPergunta:   ");

        Assert.Equal(2, changed);
        Assert.Equal("A urna que mudou o Brasil", edited.Title);
        Assert.Equal("Por que só o Brasil?", edited.Hook);
        Assert.Equal("Ela funciona sem internet, de propósito.", edited.Sections[1].Text);
        Assert.Equal(["voting machine offline"], edited.Sections[1].SearchEn);
        Assert.Equal(Script.Sections[0], edited.Sections[0]);
        Assert.Equal("Você prefere urna ou papel?", edited.Question);
        Assert.Equal(0, ScriptReview.Apply(Script, "ficou ótimo").Changed);
    }

    [Fact]
    public void The_cover_text_is_shown_and_can_be_edited_like_the_other_lines()
    {
        var script = Script with { ThumbnailText = "A urna *sem* internet" };
        Assert.Contains("\nCapa: A urna *sem* internet\n", ScriptReview.Message(Run, script).ReplaceLineEndings("\n"));
        // A script without the field (written before it existed) shows no empty line.
        Assert.DoesNotContain("\nCapa:", ScriptReview.Message(Run, Script).ReplaceLineEndings("\n"));

        var (edited, changed) = ScriptReview.Apply(script, "Capa: A urna que *ninguém* invadiu");

        Assert.Equal(1, changed);
        Assert.Equal("A urna que *ninguém* invadiu", edited.ThumbnailText);
        Assert.Equal(script.Title, edited.Title);

        // The painted picture's scene too.
        var painted = script with { ThumbnailScene = "A voting booth in a school gym" };
        Assert.Contains("\nCena: A voting booth in a school gym\n", ScriptReview.Message(Run, painted).ReplaceLineEndings("\n"));
        Assert.Equal("A quiet town square at dusk", ScriptReview.Apply(painted, "Cena: A quiet town square at dusk").Script.ThumbnailScene);
    }

    [Fact]
    public void Settings_persist_and_status_reports_them_with_what_waits_for_the_owner()
    {
        using var temp = new TempFolder();
        var state = new CloudState(temp.Path);
        Assert.Equal(new BotSettings(), state.Settings());
        state.SaveSettings(new BotSettings { Review = true, Paused = true });
        state.Save(Run with { Title = "Como surgiu a urna" });
        state.Save(new RunRecord { Tag = "20261028-0700", Article = "Magna Carta", Title = "A Magna Carta", Status = RunStatus.Failed, Note = "no voice",
            UpdatedAt = PublishData.Now });

        var text = CloudFlow.StatusMessage(state.Settings(), state.Runs(), [], PublishData.Now.AddMinutes(10));

        Assert.True(state.Settings().Paused);
        Assert.Contains("pausados", text);
        Assert.Contains("Revisão de roteiro ligada", text);
        Assert.Contains("Roteiros esperando você:\n• Como surgiu a urna", text.ReplaceLineEndings("\n"));
        Assert.Contains("A Magna Carta (27/10 15:00): no voice", text);
        Assert.Contains("Fila vazia", text);
    }

    [Fact]
    public void Status_says_when_each_next_topic_is_made()
    {
        using var temp = new TempFolder();
        var state = new CloudState(temp.Path);
        var queue = new[] { "Magna Carta", "Lei Áurea", "Apollo 11", "Sputnik 1" }
            .Select((a, i) => new QueueEntry($"b-{i}", new BacklogTopic(a, 1900, "x"), QueueOrigin.Backlog)).ToList();
        // Tuesday 27/10 15:30 in Brasília: the next renders are 19h today, then 11h and 19h tomorrow.
        var now = new DateTimeOffset(2026, 10, 27, 15, 30, 0, TimeSpan.FromHours(-3));

        var text = CloudFlow.StatusMessage(new BotSettings(), [], queue, now).ReplaceLineEndings("\n");

        Assert.Contains("\n1. Magna Carta: hoje às 19h\n2. Lei Áurea: amanhã às 11h\n3. Apollo 11: amanhã às 19h", text);
        Assert.DoesNotContain("Sputnik", text);
        Assert.Contains("vai ao ar quando você toca ✅ Publicar", text);
        Assert.Contains("1. Magna Carta (pausado)", CloudFlow.StatusMessage(new BotSettings { Paused = true }, [], queue, now));
        Assert.Equal("sexta às 07h", CloudFlow.When(new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero), now));
    }

    [Fact]
    public async Task Stats_read_the_published_shorts_from_youtube_and_rank_them_by_views()
    {
        var runs = new[]
        {
            new RunRecord { Tag = "20261027-1800", Article = "a", Status = RunStatus.Published, YouTubeUrl = "https://youtube.com/shorts/vid123", UpdatedAt = PublishData.Now },
            new RunRecord { Tag = "20261027-1900", Article = "b", Status = RunStatus.Published, YouTubeUrl = "https://youtube.com/shorts/abc456", UpdatedAt = PublishData.Now },
            new RunRecord { Tag = "20261027-2000", Article = "c", Status = RunStatus.Review, YouTubeUrl = "https://youtube.com/shorts/zzz789", UpdatedAt = PublishData.Now },
        };
        var handler = new FakeHandler((request, _) => FakeHandler.Json(HttpStatusCode.OK, """
            {"items":[
              {"id":"vid123","snippet":{"title":"Titanic","publishedAt":"2026-10-25T18:00:00Z"},"statistics":{"viewCount":"1200","likeCount":"80","commentCount":"9"}},
              {"id":"abc456","snippet":{"title":"Urna","publishedAt":"2026-10-27T18:00:00Z"},"statistics":{"viewCount":"15300","likeCount":"900"}}
            ]}
            """));

        var ids = YouTubeStats.Ids(runs);
        var stats = await YouTubeStats.FetchAsync(new HttpClient(handler), "token", ids, CancellationToken.None);
        var text = YouTubeStats.Message(stats, PublishData.Now);

        Assert.Equal(["vid123", "abc456"], ids);
        Assert.Contains("id=vid123%2Cabc456", handler.Requests[0].Request.RequestUri!.AbsoluteUri.Replace(",", "%2C"));
        Assert.Equal("Bearer token", handler.Requests[0].Request.Headers.Authorization!.ToString());
        Assert.StartsWith("📊 2 vídeo(s) · 16.500 visualizações · 980 curtidas · 9 comentários", text);
        Assert.True(text.IndexOf("Urna", StringComparison.Ordinal) < text.IndexOf("Titanic", StringComparison.Ordinal));
        Assert.Contains("Urna: 15.300 views", text);
    }
}
