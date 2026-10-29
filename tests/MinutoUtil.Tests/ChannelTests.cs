using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MinutoUtil.Core;
using MinutoUtil.Core.Cloud;
using MinutoUtil.Core.Publishing;
using MinutoUtil.Core.Video;
using MinutoUtil.Core.Voice;

namespace MinutoUtil.Tests;

/// <summary>
/// A repository root with the real Minuto Útil channel and a minimal second channel ("test-spoiler", prefix SPOILER),
/// to prove two channels never share names, secrets, sign-ins or state.
/// </summary>
internal static class TwoChannels
{
    public const string Other = "test-spoiler";

    public static string Root(TempFolder temp)
    {
        var minuto = Path.Combine(temp.Path, "channels", Channel.DefaultId);
        Directory.CreateDirectory(minuto);
        File.Copy(Path.Combine(TestPaths.RepositoryRoot, "channels", Channel.DefaultId, "config.json"), Path.Combine(minuto, "config.json"));
        WriteOther(temp.Path, "");
        return temp.Path;
    }

    /// <summary>The second channel's config; <paramref name="extra"/> is spliced into its "channel" object to break it.</summary>
    public static void WriteOther(string root, string extra)
    {
        var folder = Path.Combine(root, "channels", Other);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "config.json"), $$"""
            {
              "channel": {
                "name": "Profissão Teste",
                "envPrefix": "SPOILER",
                "tokenFolder": "%LOCALAPPDATA%/MinutoUtil/channels/test-spoiler",
                "stateFolder": "channels/test-spoiler",
                "releasePrefix": "run-test-spoiler-",
                "scheduleUtcHours": [12, 21]{{extra}}
              },
              "youtube": { "channelId": "UCtestspoiler000000000000" }
            }
            """);
    }
}

// With the other tests that set the state variables, so none of them runs at the same time.
[Collection("environment")]
public sealed class ChannelTests
{
    private static Channel Minuto => PublishData.Minuto;

    [Fact]
    public void Minuto_resolves_to_exactly_the_settings_it_had_before_channels()
    {
        // Fixtures/minuto-config-before-channels.json is config/minuto-util.json as the app loaded it before the split
        // (every default filled in). Each of its values must come out the same from channels/minuto-util/config.json.
        var before = JsonNode.Parse(File.ReadAllText(TestPaths.Fixture("minuto-config-before-channels.json")))!;
        var now = JsonNode.Parse(JsonSerializer.Serialize(Minuto.Config, Json.Options))!;
        var differences = new List<string>();
        Compare(before, now, "", differences);
        Assert.Empty(differences);
    }

    private static void Compare(JsonNode? before, JsonNode? now, string path, List<string> differences)
    {
        if (before is JsonObject obj)
        {
            foreach (var (key, value) in obj) Compare(value, now is JsonObject o ? o[key] : null, $"{path}.{key}", differences);
            return;
        }
        if (!JsonNode.DeepEquals(before, now)) differences.Add($"{path}: {before?.ToJsonString()} became {now?.ToJsonString() ?? "missing"}");
    }

    [Fact]
    public void Minuto_keeps_every_secret_name_sign_in_file_and_binding_of_the_live_channel()
    {
        var c = Minuto.Config;
        Assert.Equal(Channel.DefaultId, c.Channel.Id);
        Assert.Equal("Minuto Útil", c.Channel.Name);
        Assert.Equal("Minuto Útil", c.Video.Style.WatermarkText);
        Assert.Equal("UCNath7BzjeX3ehJCA7cNYAA", c.YouTube.ChannelId);
        Assert.Equal(
            ["MINUTO_TELEGRAM_BOT_TOKEN", "MINUTO_TELEGRAM_CHAT_ID", "MINUTO_GOOGLE_CLIENT_ID", "MINUTO_GOOGLE_CLIENT_SECRET",
             "MINUTO_YOUTUBE_REFRESH_TOKEN", "MINUTO_TIKTOK_CLIENT_KEY", "MINUTO_TIKTOK_CLIENT_SECRET", "MINUTO_TIKTOK_REFRESH_TOKEN",
             "MINUTO_STATE_KEY", "MINUTO_STATE_DIR"],
            [c.Telegram.BotTokenEnvVar, c.Telegram.ChatIdEnvVar, c.YouTube.ClientIdEnvVar, c.YouTube.ClientSecretEnvVar,
             c.YouTube.RefreshTokenEnvVar, c.TikTok.ClientKeyEnvVar, c.TikTok.ClientSecretEnvVar, c.TikTok.RefreshTokenEnvVar,
             c.Channel.StateKeyEnvVar, c.Channel.StateDirEnvVar]);
        Assert.Equal("%LOCALAPPDATA%/MinutoUtil/client_secret.json", c.YouTube.ClientSecretFile);
        Assert.Equal("%LOCALAPPDATA%/MinutoUtil/youtube-token.bin", c.YouTube.TokenFile);
        Assert.Equal("%LOCALAPPDATA%/MinutoUtil/tiktok-token.bin", c.TikTok.TokenFile);
        // The DPAPI entropy and the AES-GCM associated data of the existing sign-ins and sealed files.
        Assert.Equal("minuto-util-youtube-token"u8.ToArray(), TokenStores.Binding(c.Channel.Id, "youtube"));
        Assert.Equal("minuto-util-tiktok-token"u8.ToArray(), TokenStores.Binding(c.Channel.Id, "tiktok"));
        // The state stays at the root of the state branch, the releases keep their names, the schedule its hours.
        Assert.Equal(Path.GetFullPath("state"), Minuto.StateFolder("state"));
        Assert.Equal("run-", c.Channel.ReleasePrefix);
        Assert.Equal([14, 22], c.Channel.ScheduleUtcHours);
        Assert.Equal([9], c.Channel.IdeasUtcHours);
        Assert.Equal(Path.Combine(TestPaths.RepositoryRoot, "channels", "minuto-util", "prompts", "writer.md"), Minuto.WriterPromptFile);
        Assert.True(File.Exists(Minuto.WriterPromptFile) && File.Exists(Minuto.TrendsPromptFile) && File.Exists(Minuto.TopicsFile));
    }

    [Fact]
    public void A_token_file_sealed_before_channels_still_opens_for_minuto_and_never_for_another_channel()
    {
        using var temp = new TempFolder();
        var key = RandomNumberGenerator.GetBytes(32);
        var path = Path.Combine(temp.Path, "tiktok-token.enc");
        // Written the way the store wrote it before channels: nonce, tag, cipher text, "minuto-util-tiktok-token" bound in.
        var plain = Encoding.UTF8.GetBytes("rft.live-value");
        var data = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(data.AsSpan(0, 12));
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(data.AsSpan(0, 12), plain, data.AsSpan(28), data.AsSpan(12, 16), "minuto-util-tiktok-token"u8);
        File.WriteAllBytes(path, data);

        Assert.Equal("rft.live-value", new SealedFileTokenStore(path, key, "tiktok", "rft.seed", "minuto-util", "MINUTO_STATE_KEY").Load());
        // Same key and file, another channel: it falls back to its own secret.
        Assert.Equal("rft.seed", new SealedFileTokenStore(path, key, "tiktok", "rft.seed", TwoChannels.Other, "SPOILER_STATE_KEY").Load());
    }

    [Fact]
    public void A_dpapi_sign_in_of_one_channel_does_not_open_as_another_channels()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "youtube-token.bin");
        new DpapiTokenStore(path, "youtube", "minuto-util").Save("1//refresh");
        Assert.Equal("1//refresh", new DpapiTokenStore(path, "youtube", "minuto-util").Load());
        Assert.Throws<PublishAuthException>(() => new DpapiTokenStore(path, "youtube", TwoChannels.Other).Load());
    }

    [Fact]
    public void A_second_channel_gets_its_own_names_folders_and_watermark()
    {
        using var temp = new TempFolder();
        var root = TwoChannels.Root(temp);
        var other = Channel.Load(root, TwoChannels.Other).Config;
        var minuto = Channel.Load(root).Config;

        Assert.Equal([Channel.DefaultId, TwoChannels.Other], Channel.Ids(root));
        Assert.Equal("SPOILER_TELEGRAM_BOT_TOKEN", other.Telegram.BotTokenEnvVar);
        Assert.Equal("SPOILER_TELEGRAM_CHAT_ID", other.Telegram.ChatIdEnvVar);
        Assert.Equal("SPOILER_YOUTUBE_REFRESH_TOKEN", other.YouTube.RefreshTokenEnvVar);
        Assert.Equal("SPOILER_TIKTOK_REFRESH_TOKEN", other.TikTok.RefreshTokenEnvVar);
        Assert.Equal("SPOILER_STATE_KEY", other.Channel.StateKeyEnvVar);
        Assert.Equal("%LOCALAPPDATA%/MinutoUtil/channels/test-spoiler/youtube-token.bin", other.YouTube.TokenFile);
        Assert.Equal("Profissão Teste", other.Video.Style.WatermarkText);
        Assert.NotEqual(TokenStores.Binding(minuto.Channel.Id, "youtube"), TokenStores.Binding(other.Channel.Id, "youtube"));

        // Nothing the two channels name is shared, apart from the free keys both may use (Gemini, OpenRouter...).
        string[] Names(AppConfig c) =>
        [
            c.Telegram.BotTokenEnvVar, c.Telegram.ChatIdEnvVar, c.YouTube.ClientIdEnvVar, c.YouTube.ClientSecretEnvVar,
            c.YouTube.RefreshTokenEnvVar, c.YouTube.TokenFile, c.YouTube.ClientSecretFile, c.TikTok.ClientKeyEnvVar,
            c.TikTok.ClientSecretEnvVar, c.TikTok.RefreshTokenEnvVar, c.TikTok.TokenFile, c.Channel.StateKeyEnvVar,
            c.Channel.StateDirEnvVar, c.Channel.StateFolder, c.Channel.ReleasePrefix, c.YouTube.ChannelId,
        ];
        Assert.Empty(Names(minuto).Intersect(Names(other)));
    }

    [Fact]
    public void Two_channels_in_one_state_checkout_never_see_each_others_files()
    {
        using var temp = new TempFolder();
        var root = TwoChannels.Root(temp);
        var checkout = Path.Combine(temp.Path, "state");
        var minuto = new CloudState(Channel.Load(root).StateFolder(checkout));
        var other = new CloudState(Channel.Load(root, TwoChannels.Other).StateFolder(checkout));
        var backlog = new TopicBacklog([new("Magna Carta", 1215, "x"), new("Apollo 11", 1969, "x")]);
        var now = PublishData.Now;

        // The same tag, idea date, idea id and queue move in both, as two channels rendering in the same minute would.
        foreach (var (state, article) in new[] { (minuto, "Lei Áurea"), (other, "Half-Life 2") })
        {
            state.Save(new RunRecord { Tag = "20261029-1400", Article = article, Status = RunStatus.Review, UpdatedAt = now });
            state.SaveIdeas(new IdeaList("20261029", now, [new BacklogTopic(article, 2000, "x")]));
            state.SaveApproved(new ApprovedIdea("20261029-01", new BacklogTopic(article + " idea", 2000, "x"), FromText: false, now));
            state.SaveSettings(new BotSettings { Paused = state == other });
            File.WriteAllText(state.QuotaFile, article);
        }
        other.Move(backlog, "b-apollo-11", 1);

        // Minuto Útil's files are where the live state branch has them; the other channel's under channels/<id>.
        Assert.True(File.Exists(Path.Combine(checkout, "runs", "20261029-1400.json")));
        Assert.True(File.Exists(Path.Combine(checkout, "channels", TwoChannels.Other, "runs", "20261029-1400.json")));
        Assert.Equal(Path.Combine(checkout, "quota-state.json"), minuto.QuotaFile);
        Assert.Equal("Lei Áurea", File.ReadAllText(minuto.QuotaFile));
        Assert.Equal("Half-Life 2", File.ReadAllText(other.QuotaFile));

        Assert.Equal("Lei Áurea", Assert.Single(minuto.Runs()).Article);
        Assert.Equal("Half-Life 2", Assert.Single(other.Runs()).Article);
        Assert.Equal("Lei Áurea", Assert.Single(minuto.Ideas("20261029")!.Ideas).Article);
        Assert.Equal("Lei Áurea idea", Assert.Single(minuto.Approved()).Topic.Article);
        Assert.False(minuto.Settings().Paused);
        Assert.True(other.Settings().Paused);
        Assert.Equal(["Lei Áurea", "Lei Áurea idea"], minuto.TakenArticles().Order());
        // Only the other channel's queue was reordered.
        Assert.Equal("Lei Áurea idea", minuto.Queue(backlog)[0].Topic.Article);
        Assert.Equal("Apollo 11", other.Queue(backlog)[0].Topic.Article);
    }

    [Fact]
    public void Each_channel_seals_its_tokens_in_its_own_state_folder_under_its_own_key_name()
    {
        using var temp = new TempFolder();
        var root = TwoChannels.Root(temp);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var minuto = Channel.Load(root).Config.Channel;
        var other = Channel.Load(root, TwoChannels.Other).Config.Channel;
        var variables = new[] { minuto.StateDirEnvVar, minuto.StateKeyEnvVar, other.StateDirEnvVar, other.StateKeyEnvVar };
        Assert.All(variables, v => Assert.Null(Environment.GetEnvironmentVariable(v)));
        try
        {
            // Only the other channel's state is set: Minuto Útil's stores are untouched by it.
            Environment.SetEnvironmentVariable(other.StateDirEnvVar, Path.Combine(temp.Path, "state", "channels", TwoChannels.Other));
            Environment.SetEnvironmentVariable(other.StateKeyEnvVar, key);
            var sealedStore = Assert.IsType<SealedFileTokenStore>(TokenStores.For(other, "SPOILER_TEST_NOT_SET", "%TEMP%/none.bin", "tiktok"));
            Assert.Equal(Path.Combine(temp.Path, "state", "channels", TwoChannels.Other, "tiktok-token.enc"), sealedStore.Path);
            Assert.IsNotType<SealedFileTokenStore>(TokenStores.For(minuto, "MINUTO_TEST_NOT_SET", "%TEMP%/none.bin", "tiktok"));
        }
        finally
        {
            foreach (var v in variables) Environment.SetEnvironmentVariable(v, null);
        }
    }

    [Theory]
    [InlineData("../minuto-util")]
    [InlineData("Minuto")]
    [InlineData("minuto util")]
    [InlineData("-x")]
    [InlineData("")]
    public void Bad_channel_ids_are_refused_before_any_path_is_made(string id) =>
        Assert.Throws<ArgumentException>(() => Channel.Load(TestPaths.RepositoryRoot, id));

    [Fact]
    public void An_unknown_channel_says_which_ones_exist() =>
        Assert.Contains("minuto-util", Assert.Throws<ArgumentException>(() => Channel.Load(TestPaths.RepositoryRoot, "nope")).Message);

    [Theory]
    [InlineData(""", "stateFolder": "." """)]
    [InlineData(""", "stateFolder": "../x" """)]
    [InlineData(""", "envPrefix": "spoiler" """)]
    [InlineData(""", "releasePrefix": "run-" """)]
    [InlineData(""", "name": "" """)]
    [InlineData(""", "tokenFolder": "" """)]
    [InlineData(""", "id": "minuto-util" """)]
    [InlineData(""", "scheduleUtcHours": [24] """)]
    public void A_config_that_could_mix_with_another_channel_is_refused(string extra)
    {
        using var temp = new TempFolder();
        var root = TwoChannels.Root(temp);
        // A repeated key in the JSON object: the last one wins, so this replaces the good value.
        TwoChannels.WriteOther(root, extra);
        Assert.ThrowsAny<Exception>(() => Channel.Load(root, TwoChannels.Other));
    }

    [Fact]
    public void The_real_channels_share_no_secret_name_state_folder_or_sign_in()
    {
        var all = Channel.Ids(TestPaths.RepositoryRoot).Select(id => Channel.Load(TestPaths.RepositoryRoot, id).Config).ToList();
        Assert.Contains(all, c => c.Channel.Id == Channel.DefaultId);
        foreach (var pick in new Func<AppConfig, string>[]
                 {
                     c => c.Channel.EnvPrefix, c => c.Channel.StateFolder, c => c.Channel.TokenFolder, c => c.Channel.ReleasePrefix,
                     c => c.YouTube.ChannelId, c => c.Telegram.BotTokenEnvVar,
                 })
            Assert.Equal(all.Count, all.Select(pick).Distinct().Count());
    }

    /// <summary>The cron lines of a workflow as UTC hours; every line is daily at minute 0.</summary>
    private static List<int> CronHours(string workflow)
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, ".github", "workflows", workflow));
        var lines = Regex.Matches(text, @"- cron: ""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        Assert.All(lines, l => Assert.Matches(@"^0 \d{1,2} \* \* \*$", l));
        return lines.Select(l => int.Parse(l.Split(' ')[1])).ToList();
    }

    [Fact]
    public void The_render_and_idea_schedules_match_the_channel_configs()
    {
        var all = Channel.Ids(TestPaths.RepositoryRoot).Select(id => Channel.Load(TestPaths.RepositoryRoot, id).Config.Channel).ToList();
        var render = CronHours("render.yml");
        Assert.Equal(render.Count, render.Distinct().Count());
        Assert.Equal(all.SelectMany(c => c.ScheduleUtcHours).Distinct().Order(), render.Order());
        var ideas = CronHours("ideas.yml");
        Assert.Equal(all.SelectMany(c => c.IdeasUtcHours).Distinct().Order(), ideas.Order());
        // Minuto Útil's two renders stay at 14:00 and 22:00 UTC (11:00 and 19:00 in Brasília), its list at 09:00;
        // Profissão Spoiler's an hour later, its list at the same time.
        var minuto = Channel.Load(TestPaths.RepositoryRoot, "minuto-util").Config.Channel;
        Assert.Equal([14, 22], minuto.ScheduleUtcHours);
        Assert.Equal([9], minuto.IdeasUtcHours);
        var spoiler = Channel.Load(TestPaths.RepositoryRoot, "profissao-spoiler").Config.Channel;
        Assert.Equal([15, 23], spoiler.ScheduleUtcHours);
        Assert.Equal([9], spoiler.IdeasUtcHours);
    }

    [Fact]
    public void Workflows_take_channel_secrets_only_through_the_channel_prefix()
    {
        // A channel secret written by name (secrets.MINUTO_X) would hand Minuto Útil's secret to every channel.
        var shared = new HashSet<string> { "GEMINI_API_KEY", "OPENROUTER_API_KEY", "CLOUDFLARE_AI_TOKEN", "CLOUDFLARE_ACCOUNT_ID", "SMALLEST_API_KEY" };
        foreach (var file in Directory.GetFiles(Path.Combine(TestPaths.RepositoryRoot, ".github", "workflows"), "*.yml"))
        {
            var text = File.ReadAllText(file);
            Assert.All(Regex.Matches(text, @"secrets\.([A-Z0-9_]+)").Select(m => m.Groups[1].Value), s => Assert.Contains(s, shared));
            Assert.DoesNotContain("MINUTO_", text);
            // Every channel run reaches the app through the wrapper that renames CH_ variables and adds --channel.
            Assert.DoesNotContain("minuto-util.dll", text);
        }
    }

    [Fact]
    public void Status_and_schedule_messages_come_from_the_channel()
    {
        using var temp = new TempFolder();
        var other = Channel.Load(TwoChannels.Root(temp), TwoChannels.Other).Config.Channel;
        var queue = new[] { "Magna Carta", "Apollo 11" }.Select((a, i) => new QueueEntry($"b-{i}", new BacklogTopic(a, 1900, "x"), QueueOrigin.Backlog)).ToList();
        var now = new DateTimeOffset(2026, 10, 27, 15, 30, 0, TimeSpan.FromHours(-3));

        var minuto = CloudFlow.StatusMessage(Minuto.Config.Channel, new BotSettings(), [], queue, now).ReplaceLineEndings("\n");
        Assert.StartsWith("ℹ️ Minuto Útil\n▶️ Vídeos às 11h e 19h (/pausar para).", minuto);
        Assert.Contains("1. Magna Carta: hoje às 19h\n2. Apollo 11: amanhã às 11h", minuto);

        var spoiler = CloudFlow.StatusMessage(other, new BotSettings(), [], queue, now).ReplaceLineEndings("\n");
        Assert.StartsWith("ℹ️ Profissão Teste\n▶️ Vídeos às 09h e 18h (/pausar para).", spoiler);
        Assert.Contains("1. Magna Carta: hoje às 18h\n2. Apollo 11: amanhã às 09h", spoiler);

        var none = CloudFlow.StatusMessage(other with { ScheduleUtcHours = [] }, new BotSettings(), [], queue, now);
        Assert.Contains("Sem vídeos agendados", none);
        Assert.Contains("1. Magna Carta\n", none.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void The_watermark_and_the_cover_show_the_channels_name()
    {
        using var temp = new TempFolder();
        var other = Channel.Load(TwoChannels.Root(temp), TwoChannels.Other).Config.Video;
        var timings = new SpeechTimings(1, [new SectionTime(0, 1)], [new SpokenWord("Oi", 0, 0.5, 0)], []);
        var captions = CaptionBuilder.Build(timings, 1, other.FontName, other.StampFontName, "", "", 0, other.Style, emblem: true);
        Assert.Contains(@"\alpha&H58&}Profissão Teste", captions);
        Assert.DoesNotContain("Minuto", captions);
        var cover = Thumbnail.Ass(Thumbnail.Arrange(Thumbnail.Words("O *fim*"), "1998"), other.FontName, other.StampFontName, other.Style, emblem: true);
        Assert.Contains("}Profissão Teste", cover);
        Assert.DoesNotContain("Minuto", cover);
    }
}
