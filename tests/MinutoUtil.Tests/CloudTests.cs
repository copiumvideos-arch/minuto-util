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
            Assert.InRange(t.Year, 1, 2025);
        });
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
        Assert.Contains(messages.Sent, m => m.Text.StartsWith("Como a Previsão do Tempo Mudou o Dia D\n\n#segundaguerra", StringComparison.Ordinal));
        Assert.Contains("https://youtube.com/shorts/vid123", messages.Sent[^1].Text);

        var requests = youtube.Requests.Count + tiktok.Requests.Count;
        Assert.Equal(0, await flow.PublishAsync(Tag, folder, Publisher(youtube, tiktok, messages), CancellationToken.None));
        Assert.Equal(requests, youtube.Requests.Count + tiktok.Requests.Count);
        Assert.StartsWith("Já publicado", messages.Sent[^1].Text);
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
        Assert.Equal(["pub:" + Tag, "rej:" + Tag], CloudFlow.ReviewButtons(Tag).Select(b => b.Data));
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
}
