using System.Net;
using System.Text;
using MinutoUtil.Core;
using MinutoUtil.Core.Cloud;
using MinutoUtil.Core.Media;
using MinutoUtil.Core.Publishing;
using MinutoUtil.Core.Scripts;
using MinutoUtil.Core.Sources;
using MinutoUtil.Core.Video;
using MinutoUtil.Core.Voice;
using Microsoft.Extensions.Time.Testing;

namespace MinutoUtil.Tests;

/// <summary>
/// What keeps Profissão Spoiler's slots filled (2026-11-16): a game with no reachable trailers is dropped before any
/// Gemini request and the run moves on, a finished video survives a slow Telegram upload, and the voice says English
/// names from the writer's respellings while the captions keep the real spelling.
/// </summary>
public sealed class RetryAndPronunciationTests
{
    private const string Tag = "20261116-1400";

    private static VideoScript Script(string hook, params string[] sections) =>
        new("Título", hook, "", "", sections.Select(t => new ScriptSection(t, [], [], "")).ToList(), "", []);

    [Fact]
    public void The_writers_respellings_reach_the_voice_only_for_words_in_the_narration_and_in_plain_letters()
    {
        var script = Script("Por que The Witcher quase não saiu?", "A CD Projekt Red apostou tudo em The Witcher três.", "E o GTA também.")
            with
            {
                Pronunciations =
                [
                    new("The Witcher", "dê uítcher"),
                    new("CD Projekt Red", "cê dê prôjekt réd"),
                    new("Witcher", "uítcher"),
                    new("Elden Ring", "élden ríngui"), // not in the narration
                    new("GTA", "djí tí ei"), // the channel's own respelling wins
                    new("Red", "r3d"), // digits: dropped
                    new("CD", "<speak>cê dê</speak>"), // markup: dropped
                    new("", "nada"),
                ],
            };
        var configured = new Dictionary<string, string> { ["GTA"] = "gê tê á" };

        var respellings = SpokenText.Respellings(script, configured, fromScript: true);

        Assert.Equal(["CD Projekt Red", "The Witcher", "Witcher", "GTA"], respellings.Select(r => r.Key));
        Assert.Equal("gê tê á", respellings.Single(r => r.Key == "GTA").Value);
        Assert.Equal("Por que dê uítcher quase não saiu?", SpokenText.Pronounce(script.Hook, respellings));
        Assert.Equal("A cê dê prôjekt réd apostou tudo em dê uítcher três.", SpokenText.Pronounce(script.Sections[0].Text, respellings));

        // Minuto Útil keeps only its configured list, whatever the writer sends.
        Assert.Equal(["GTA"], SpokenText.Respellings(script, configured, fromScript: false).Select(r => r.Key));
    }

    [Fact]
    public void A_script_from_before_respellings_still_loads()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<VideoScript>(
            """{"title":"T","hook":"H","eraStamp":"","placeStamp":"","sections":[],"description":"","hashtags":[]}""", Json.Options)!;
        Assert.Empty(old.Pronunciations);
        Assert.Empty(SpokenText.Respellings(old, new Dictionary<string, string>(), fromScript: true));
    }

    [Fact]
    public void Only_the_profissao_spoiler_voice_uses_the_writers_respellings()
    {
        Assert.True(Channel.Load(TestPaths.RepositoryRoot, "profissao-spoiler").Config.Voice.ScriptPronunciations);
        Assert.False(Channel.Load(TestPaths.RepositoryRoot, "minuto-util").Config.Voice.ScriptPronunciations);
    }

    [Fact]
    public void The_review_caption_shows_what_the_voice_respelled()
    {
        var run = new RunRecord { Tag = Tag, Article = "The Witcher 3: Wild Hunt", Year = 2015, Topic = "Curiosidades de games",
            Status = RunStatus.Rendering, UpdatedAt = PublishData.Now };
        var log = new[] { $"14:20:01 {Narrator.RespelledPrefix}The Witcher = dê uítcher; GTA = gê tê á" };

        var caption = CloudFlow.ReviewCaption(run, PublishData.Metadata(), log);

        Assert.Contains("🗣️ Pronúncia: The Witcher = dê uítcher; GTA = gê tê á", caption);
    }

    /// <summary>Steam's store search as GitHub's runners meet it: an empty answer for a Nintendo game, or a 503.</summary>
    private static GameTrailers Trailers(HttpStatusCode status, string body) =>
        new(new SteamTrailers(new HttpClient(new FakeHandler((_, _) => FakeHandler.Json(status, body))), new Ffmpeg(new VideoOptions()), new MediaOptions()),
            null, new MediaOptions());

    [Fact]
    public async Task A_game_no_store_sells_has_no_footage_but_a_store_that_is_down_is_only_a_failure()
    {
        using var temp = new TempFolder();

        var none = await Assert.ThrowsAsync<NoFootageException>(() =>
            Trailers(HttpStatusCode.OK, """{"total":0,"items":[]}""").GatherAsync("The Legend of Zelda: Ocarina of Time", temp.Path, CancellationToken.None));
        Assert.Contains("Steam has no game named", none.Message);
        Assert.Contains("minuto-util footage", none.Message);

        var down = await Assert.ThrowsAsync<MediaException>(() =>
            Trailers(HttpStatusCode.ServiceUnavailable, "{}").GatherAsync("Fallout 4", temp.Path, CancellationToken.None));
        Assert.IsNotType<NoFootageException>(down);
        Assert.True(down.Transient);
    }

    [Fact]
    public async Task A_game_with_no_footage_fails_before_the_sources_or_the_writer()
    {
        using var temp = new TempFolder();
        var root = TestPaths.RepositoryRoot;
        var tools = Path.Combine(root, "tools");
        var voice = new VoiceOptions();
        var usage = new RunUsage();
        var config = new AppConfig { Media = new MediaOptions { Footage = "trailers" }, Story = new StoryOptions { EnglishArticle = "Super Mario 64" } };
        var pipeline = new Pipeline(config, root,
            new WikipediaClient(new HttpClient(new FakeHandler((_, _) => throw new InvalidOperationException("no source may be read")))),
            () => throw new InvalidOperationException("no script may be written"),
            new Narrator(voice, tools, null, new KokoroVoice(tools, voice), usage),
            new MediaGatherer([], [], null!, null!, new MediaOptions(), 1996), trailers: Trailers(HttpStatusCode.OK, """{"total":0,"items":[]}"""));

        await Assert.ThrowsAsync<NoFootageException>(() => pipeline.RunAsync(Path.Combine(temp.Path, "run"), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(temp.Path, "run", "sources.json")));
    }

    private sealed class Messages
    {
        public List<string> Sent { get; } = [];

        public Task Send(string text, IReadOnlyList<TelegramButton> buttons, CancellationToken ct)
        {
            Sent.Add(text);
            return Task.CompletedTask;
        }
    }

    private static readonly TopicBacklog Backlog = new(
    [
        new BacklogTopic("The Legend of Zelda: Ocarina of Time", 1998, "Curiosidades de games"),
        new BacklogTopic("Fallout 4", 2015, "Curiosidades de games"),
    ]);

    [Fact]
    public async Task A_game_with_no_footage_leaves_the_queue_at_once_and_the_next_game_is_up()
    {
        using var temp = new TempFolder();
        var state = new CloudState(Path.Combine(temp.Path, "state"));
        var messages = new Messages();
        var flow = new CloudFlow(state, messages.Send, new FakeTimeProvider(PublishData.Now));
        Assert.Equal("The Legend of Zelda: Ocarina of Time", state.NextTopic(Backlog).Next!.Article);
        state.Save(new RunRecord { Tag = Tag, Article = "The Legend of Zelda: Ocarina of Time", Topic = "Curiosidades de games",
            Status = RunStatus.Rendering, UpdatedAt = PublishData.Now });

        await flow.NoFootageAsync(Tag, "profissao-spoiler", Backlog, "Steam has no game named ...", CancellationToken.None);

        // One failure is enough (a second try would find the same stores), and the same run goes on with Fallout 4.
        Assert.Equal("Fallout 4", state.NextTopic(Backlog).Next!.Article);
        Assert.Equal(RunStatus.Failed, state.Find(Tag)!.Status);
        var sent = Assert.Single(messages.Sent);
        Assert.Contains("Pulei The Legend of Zelda: Ocarina of Time", sent);
        Assert.Contains("minuto-util footage --channel profissao-spoiler --article \"The Legend of Zelda: Ocarina of Time\"", sent);
        Assert.Contains("Indo para o próximo jogo", sent);

        await flow.NoFootageAsync(Tag, "profissao-spoiler", Backlog, "again", CancellationToken.None, last: true);
        Assert.Contains("o próximo horário segue a fila", messages.Sent[^1]);
    }

    [Fact]
    public async Task A_failed_run_that_starts_again_says_so()
    {
        using var temp = new TempFolder();
        var state = new CloudState(Path.Combine(temp.Path, "state"));
        var messages = new Messages();
        var flow = new CloudFlow(state, messages.Send, new FakeTimeProvider(PublishData.Now));
        state.Save(new RunRecord { Tag = Tag, Article = "Fallout 4", Status = RunStatus.Rendering, UpdatedAt = PublishData.Now });

        await flow.FailedAsync(Tag, "No Gemini model answered", null, CancellationToken.None, retrying: true);
        Assert.Contains("Tentando de novo agora", messages.Sent[^1]);
        // One failure keeps the game in the queue, so the retry can take it again.
        Assert.Contains("Fallout 4", state.Queue(Backlog).Select(e => e.Topic.Article));

        await flow.FailedAsync(Tag, "No Gemini model answered", null, CancellationToken.None);
        Assert.DoesNotContain("Tentando de novo", messages.Sent[^1]);
    }

    [Fact]
    public async Task A_video_upload_goes_through_the_long_client_and_is_tried_once_more_after_a_timeout_or_a_5xx()
    {
        using var temp = new TempFolder();
        var video = Path.Combine(temp.Path, "video.mp4");
        File.WriteAllBytes(video, Encoding.ASCII.GetBytes("fake-mp4-bytes"));
        var calls = 0;
        var uploads = new FakeHandler((_, _) => ++calls switch
        {
            1 => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing."),
            _ => FakeHandler.Json(HttpStatusCode.OK, """{"ok":true}"""),
        });
        var messages = new FakeHandler((_, _) => throw new InvalidOperationException("a video must not use the 30-second client"));
        var notifier = new TelegramNotifier(new HttpClient(messages), "123:secret", "42", new HttpClient(uploads));

        await notifier.SendVideoAsync(video, "legenda", [], CancellationToken.None);
        Assert.Equal(2, uploads.Requests.Count);
        Assert.Contains("fake-mp4-bytes", uploads.Requests[1].Body);

        // A 5xx gets the second try too; a 4xx (too large) fails at once, and twice 5xx gives up.
        calls = 0;
        var flaky = new FakeHandler((_, _) => ++calls == 1
            ? FakeHandler.Json(HttpStatusCode.BadGateway, """{"ok":false,"description":"Bad Gateway"}""")
            : FakeHandler.Json(HttpStatusCode.OK, """{"ok":true}"""));
        await new TelegramNotifier(new HttpClient(flaky), "123:secret", "42").SendVideoAsync(video, "x", [], CancellationToken.None);
        Assert.Equal(2, flaky.Requests.Count);

        var tooLarge = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.RequestEntityTooLarge, """{"ok":false,"description":"Request Entity Too Large"}"""));
        await Assert.ThrowsAsync<HttpRequestException>(() => new TelegramNotifier(new HttpClient(tooLarge), "123:secret", "42").SendVideoAsync(video, "x", [], CancellationToken.None));
        Assert.Single(tooLarge.Requests);

        var down = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.InternalServerError, """{"ok":false,"description":"Internal Server Error"}"""));
        var ex = await Assert.ThrowsAsync<TelegramServerException>(() => new TelegramNotifier(new HttpClient(down), "123:secret", "42").SendVideoAsync(video, "x", [], CancellationToken.None));
        Assert.Equal(TelegramNotifier.UploadAttempts, down.Requests.Count);
        Assert.DoesNotContain("secret", ex.Message);
    }
}
