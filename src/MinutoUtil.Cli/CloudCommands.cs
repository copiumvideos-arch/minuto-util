using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MinutoUtil.Core;
using MinutoUtil.Core.Cloud;
using MinutoUtil.Core.Llm;
using MinutoUtil.Core.Scripts;
using MinutoUtil.Core.Sources;
using MinutoUtil.Core.Publishing;

/// <summary>
/// The steps the GitHub Actions workflows (.github/workflows) run around `make` and `publish`, plus `cloud secrets`,
/// which copies this PC's keys and sign-ins into the repository's Actions secrets without printing them.
///   cloud ideas   --state <dir> [--again]                           sends the day's 10 ideas (from the trends)
///   cloud approve --state <dir> --date <yyyyMMdd> --index <n>        the owner tapped idea n of that day's list
///   cloud add-idea --state <dir> --idea "<text>"                    the owner's own idea, first in line
///   cloud queue   --state <dir> [--top <item> | --remove <item> | --move <item> --to <n>]   changes, then shows the queue
///   cloud settings --state <dir> --set review-on|review-off|pause|resume
///   cloud status  --state <dir>          cloud stats --state <dir>
///   cloud script-review --state <dir> --tag <tag> --run <folder>     review mode: the script to Telegram
///   cloud edit-script   --state <dir> --tag <tag> --run <folder> --text "<owner's reply>"
///   cloud title   --state <dir> --tag <tag> --run <folder> --title "<new title>"
///   cloud next    --state <dir> --tag <tag> [--article "<idea>" | --remake <old tag>]   picks the topic, prints it as JSON
///   cloud review  --state <dir> --tag <tag> --run <folder>          sends the video to Telegram with the buttons
///   cloud publish --state <dir> --tag <tag> --run <folder>          YouTube + TikTok draft for an approved run
///   cloud reject  --state <dir> --tag <tag>
///   cloud tiktok-check --state <dir>     the signed-in TikTok account and the status of the latest drafts
///   cloud failed  --state <dir> --tag <tag> --reason <text> [--log-url <url>]
///   cloud secrets --repo <owner/name>
/// </summary>
internal static class CloudCommands
{
    public static async Task<int> RunAsync(string[] args, AppConfig config, string root, Func<string, string?> key)
    {
        var step = args.ElementAtOrDefault(1) ?? "";
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            if (step == "secrets") return await SecretsAsync(config, key, Option(args, "--repo") ?? throw new ArgumentException("cloud secrets needs --repo <owner/name>."));

            var state = new CloudState(Option(args, "--state") ?? throw new ArgumentException("cloud needs --state <state branch folder>."));
            using var apiHttp = SafeHttp.CreatePublicClient(TimeSpan.FromSeconds(30));
            using var uploadHttp = SafeHttp.CreatePublicClient(TimeSpan.FromMinutes(10));
            var telegram = PublishingCommands.Telegram(config.Telegram, key, apiHttp)
                ?? throw new ArgumentException($"The cloud runs report to Telegram: set {config.Telegram.BotTokenEnvVar} and {config.Telegram.ChatIdEnvVar}.");
            switch (step)
            {
                case "ideas":
                    return await IdeasAsync(state, config, root, key, telegram, args.Contains("--again"), cts.Token);
                case "approve":
                    return Approve(state, Option(args, "--date") ?? "", int.TryParse(Option(args, "--index"), out var index) ? index : 0);
                case "add-idea":
                    return await AddIdeaAsync(state, config, root, key, Option(args, "--idea") ?? "", telegram, cts.Token);
                case "queue":
                    return await QueueAsync(state, root, Option(args, "--top"), Option(args, "--remove"), Option(args, "--move"),
                        Option(args, "--to"), telegram, cts.Token);
                case "settings":
                    return await SettingsAsync(state, Option(args, "--set") ?? "", telegram, cts.Token);
                case "status":
                    await telegram.SendTextAsync(CloudFlow.StatusMessage(state.Settings(), state.Runs(),
                        state.Queue(TopicBacklog.Load(Path.Combine(root, "config", "topics.json"))), DateTimeOffset.UtcNow), cts.Token);
                    return 0;
                case "stats":
                {
                    var token = await PublishingCommands.YouTubeAccessTokenAsync(config, key, apiHttp, cts.Token);
                    var stats = await YouTubeStats.FetchAsync(apiHttp, token, YouTubeStats.Ids(state.Runs()), cts.Token);
                    await telegram.SendTextAsync(YouTubeStats.Message(stats, DateTimeOffset.UtcNow), cts.Token);
                    return 0;
                }
                case "tiktok-check":
                {
                    var auth = PublishingCommands.StoredTikTokAuth(config, key, apiHttp);
                    var uploader = new TikTokUploader(apiHttp, auth.GetAccessTokenAsync);
                    return await new CloudFlow(state, (text, buttons, ct) => telegram.SendTextAsync(text, buttons, ct), TimeProvider.System)
                        .TikTokCheckAsync(auth.CurrentAccountAsync, uploader.FetchStatusAsync, cts.Token);
                }
            }
            var tag = RunTag.Validate(Option(args, "--tag"));
            var flow = new CloudFlow(state, (text, buttons, ct) => telegram.SendTextAsync(text, buttons, ct), TimeProvider.System);

            switch (step)
            {
                case "next":
                    if (Option(args, "--remake") is { Length: > 0 } remake)
                        return Started(state, tag, flow.Remake(RunTag.Validate(remake)), left: -1, review: state.Settings().Review);
                    if (Option(args, "--continue") is { Length: > 0 })
                    {
                        // The same run goes on after its script review: same tag, same topic.
                        var run = state.Find(tag) ?? throw new ArgumentException($"Run {tag} is not in the state branch.");
                        if (run.Status != RunStatus.Script) throw new ArgumentException($"Run {tag} is {run.Status.ToString().ToLowerInvariant()}, not waiting on its script.");
                        return Started(state, tag, new BacklogTopic(run.Article, run.Year ?? 0, run.Topic, run.Trend, run.PortugueseArticle), left: -1,
                            review: args.Contains("--rescript"));
                    }
                    if (args.Contains("--scheduled") && state.Settings().Paused)
                    {
                        Log.Info("next: the scheduled videos are paused (/retomar on Telegram)");
                        return 3;
                    }
                    return await NextAsync(state, tag, config, root, key, Option(args, "--article"), telegram, cts.Token);
                case "script-review":
                {
                    var folder = Path.GetFullPath(Option(args, "--run") ?? throw new ArgumentException("cloud script-review needs --run <folder>."));
                    var run = state.Find(tag) ?? throw new ArgumentException($"Run {tag} is not in the state branch.");
                    var script = ReadJson<VideoScript>(Path.Combine(folder, "script.json"));
                    await telegram.SendTextAsync(ScriptReview.Message(run, script), ScriptReview.Buttons(tag), cts.Token);
                    state.Save(run with { Status = RunStatus.Script, Title = script.Title, UpdatedAt = DateTimeOffset.UtcNow });
                    return 0;
                }
                case "edit-script":
                {
                    var folder = Path.GetFullPath(Option(args, "--run") ?? throw new ArgumentException("cloud edit-script needs --run <folder>."));
                    var path = Path.Combine(folder, "script.json");
                    var (edited, changed) = ScriptReview.Apply(ReadJson<VideoScript>(path), Option(args, "--text") ?? "");
                    if (changed == 0)
                    {
                        await telegram.SendTextAsync("🤔 Não reconheci nenhuma linha. Responda ao roteiro com linhas que comecem com Título:, Gancho:, 1., 2. ou Pergunta:.", cts.Token);
                        return 3;
                    }
                    // The owner's text goes through the same cleanup as a written script (dashes, digits for the voice).
                    edited = ScriptWriter.Polish(edited, ReadJson<List<SourceArticle>>(Path.Combine(folder, "sources.json")));
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(edited, Json.Options), cts.Token);
                    await telegram.SendTextAsync($"✏️ Roteiro atualizado ({changed} linha(s)). Fazendo o vídeo, chega em uns 15 minutos.", cts.Token);
                    return 0;
                }
                case "title":
                {
                    var folder = Path.GetFullPath(Option(args, "--run") ?? throw new ArgumentException("cloud title needs --run <folder>."));
                    var title = TextUtil.Truncate((Option(args, "--title") ?? "").Replace('\n', ' ').Trim(), 100);
                    if (title.Length == 0) throw new ArgumentException("The new title is empty.");
                    var run = state.Find(tag) ?? throw new ArgumentException($"Run {tag} is not in the state branch.");
                    if (run.Status != RunStatus.Review) throw new ArgumentException($"Run {tag} is {run.Status.ToString().ToLowerInvariant()}; only a video waiting for review gets a new title.");
                    var metadataPath = Path.Combine(folder, "metadata.json");
                    var metadata = ReadJson<VideoMetadata>(metadataPath) with { Title = title };
                    await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, Json.Options), cts.Token);
                    state.Save(run with { Title = title, UpdatedAt = DateTimeOffset.UtcNow });
                    await telegram.SendTextAsync($"🏷️ Novo título: {title}", cts.Token);
                    return 0;
                }
                case "review":
                {
                    var folder = Path.GetFullPath(Option(args, "--run") ?? throw new ArgumentException("cloud review needs --run <folder>."));
                    var run = state.Find(tag) ?? throw new ArgumentException($"Run {tag} is not in the state branch.");
                    var metadata = CloudFlow.ReadMetadata(folder);
                    var log = Path.Combine(folder, "run.log");
                    var caption = CloudFlow.ReviewCaption(run, metadata, File.Exists(log) ? File.ReadLines(log) : []);
                    await telegram.SendVideoAsync(Path.Combine(folder, "video.mp4"), caption, CloudFlow.ReviewButtons(tag), cts.Token);
                    state.Save(run with { Status = RunStatus.Review, Title = metadata.Title, UpdatedAt = DateTimeOffset.UtcNow });
                    Log.Info($"review: {tag} sent to Telegram");
                    return 0;
                }
                case "publish":
                {
                    var folder = Path.GetFullPath(Option(args, "--run") ?? throw new ArgumentException("cloud publish needs --run <folder>."));
                    var publisher = PublishingCommands.BuildPublisher(config, key, apiHttp, uploadHttp, telegram.SendTextAsync);
                    return await flow.PublishAsync(tag, folder, publisher, cts.Token);
                }
                case "reject":
                    await flow.RejectAsync(tag, cts.Token);
                    return 0;
                case "failed":
                    await flow.FailedAsync(tag, Option(args, "--reason") ?? "unknown error", Option(args, "--log-url"), cts.Token);
                    return 0;
                default:
                    Console.Error.WriteLine("Usage: cloud ideas|approve|add-idea|next|review|publish|reject|tiktok-check|failed --state <dir> ..., or cloud secrets --repo <owner/name>");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or PublishRefusedException or PublishAuthException or HttpRequestException
            or InvalidDataException or JsonException or IOException)
        {
            Log.Warn($"cloud {step} failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Picks the next topic and records the run as rendering, printing {"article","portugueseArticle","year","topic",
    /// "trend","left"} for the workflow. With --article (the owner's /novo text): a backlog title is used as it is,
    /// anything else is an idea the trend picker turns into an event (one model request). Exit code 3: nothing to render.
    /// </summary>
    private static async Task<int> NextAsync(CloudState state, string tag, AppConfig config, string root, Func<string, string?> key,
        string? article, TelegramNotifier telegram, CancellationToken ct)
    {
        var backlog = TopicBacklog.Load(Path.Combine(root, "config", "topics.json"));
        var (next, left) = state.NextTopic(backlog);
        if (!string.IsNullOrWhiteSpace(article))
        {
            var idea = article.Trim();
            next = backlog.Topics.FirstOrDefault(t => string.Equals(t.Article, idea, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                using var http = SafeHttp.CreatePublicClient(TimeSpan.FromSeconds(30));
                using var llmHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                try
                {
                    next = (await Picker(state, config, root, key, http, llmHttp).PickForIdeaAsync(idea, state.TakenArticles(), ct)).FirstOrDefault();
                }
                catch (Exception ex) when (ex is LlmException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    Log.Warn($"idea: no topic ({TextUtil.Truncate(ex.Message, 200)})");
                }
                if (next is null)
                {
                    await telegram.SendTextAsync($"🤔 Não achei um evento na Wikipedia para \"{TextUtil.Truncate(idea, 100)}\". Tente com outras palavras.", ct);
                    return 3;
                }
            }
        }
        if (next is null)
        {
            await telegram.SendTextAsync("📭 A fila de temas acabou (config/topics.json). Adicione eventos para continuar.", ct);
            return 3;
        }
        if (string.IsNullOrWhiteSpace(article) && left <= 5)
            await telegram.SendTextAsync($"📋 Restam {left} temas na fila (config/topics.json).", ct);
        return Started(state, tag, next, left, state.Settings().Review);
    }

    /// <summary>
    /// Records the run as rendering and prints the topic as JSON for the workflow; "review" tells it to stop after the
    /// script (review mode is on, or the owner asked for another script).
    /// </summary>
    private static int Started(CloudState state, string tag, BacklogTopic next, int left, bool review)
    {
        state.Save(new RunRecord { Tag = tag, Article = next.Article, PortugueseArticle = next.PortugueseArticle, Year = next.Year > 0 ? next.Year : null,
            Topic = next.Topic, Trend = next.Trend, Status = RunStatus.Rendering, UpdatedAt = DateTimeOffset.UtcNow });
        Console.WriteLine(JsonSerializer.Serialize(new { article = next.Article, portugueseArticle = next.PortugueseArticle ?? "", year = next.Year,
            topic = next.Topic, trend = next.Trend, left, review }));
        return 0;
    }

    private static T ReadJson<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options) ?? throw new InvalidDataException($"{path} is empty.");

    /// <summary>/revisar on|off, /pausar, /retomar.</summary>
    private static async Task<int> SettingsAsync(CloudState state, string set, TelegramNotifier telegram, CancellationToken ct)
    {
        var settings = state.Settings();
        (settings, var reply) = set switch
        {
            "review-on" => (settings with { Review = true }, "📝 Revisão de roteiro ligada: cada vídeo espera você aprovar ou editar o roteiro."),
            "review-off" => (settings with { Review = false }, "📝 Revisão de roteiro desligada: os vídeos saem direto, como antes."),
            "pause" => (settings with { Paused = true }, "⏸️ Vídeos agendados pausados. /novo ainda funciona; /retomar volta."),
            "resume" => (settings with { Paused = false }, "▶️ Vídeos agendados de volta: 07h, 11h, 15h e 19h."),
            _ => throw new ArgumentException($"Unknown setting \"{TextUtil.Truncate(set, 20)}\"."),
        };
        state.SaveSettings(settings);
        await telegram.SendTextAsync(reply, ct);
        return 0;
    }

    /// <summary>
    /// The trend picker on the writer models: busy Gemini models hand over to the next, then Cloudflare Workers AI, then OpenRouter. Five
    /// requests at most, so three Gemini models answering 429 or 503 still leave the OpenRouter spares a turn; a model
    /// out of its daily quota is remembered in the quota file on the state branch and skipped without a request.
    /// </summary>
    private static TrendPicker Picker(CloudState state, AppConfig config, string root, Func<string, string?> key, HttpClient http, HttpClient llmHttp)
    {
        var usage = new RunUsage(5);
        var quota = new QuotaTracker(state.QuotaFile, sharedPaths: config.SharedQuotaFiles);
        var models = new List<ILlmClient>();
        if (key(config.Gemini.ApiKeyEnvVar) is { } gemini) models.Add(new GeminiClient(llmHttp, gemini, config.Gemini.Models, usage, quota));
        if (key(config.CloudflareAi.TokenEnvVar) is { } cloudflare && key(config.CloudflareAi.AccountIdEnvVar) is { } account)
            models.Add(OpenRouterClient.ForCloudflare(llmHttp, cloudflare, account, config.CloudflareAi.Models, usage, quota));
        if (key(config.OpenRouter.ApiKeyEnvVar) is { } openRouter) models.Add(new OpenRouterClient(llmHttp, openRouter, config.OpenRouter.WriterModels, usage, quota));
        return new TrendPicker(new ChainedLlmClient(models), http, File.ReadAllText(Path.Combine(root, "prompts", "trends.md")),
            config.Channel.Name, TimeProvider.System);
    }

    /// <summary>
    /// Once a day: reads Google Trends Brazil and pt.wikipedia's most read pages, asks the writer models for 10 historical
    /// events related to them (one request), checks them on Wikipedia, saves the list as ideas/&lt;date&gt;.json and sends
    /// it to the owner with a button per idea. Any failure only warns: the renders go on with approved ideas and the backlog.
    /// </summary>
    private static async Task<int> IdeasAsync(CloudState state, AppConfig config, string root, Func<string, string?> key,
        TelegramNotifier telegram, bool again, CancellationToken ct)
    {
        var date = IdeaList.DateFor(DateTimeOffset.UtcNow);
        if (!again && state.Ideas(date) is not null)
        {
            Log.Info($"ideas: the list for {date} was already sent");
            return 0;
        }
        using var http = SafeHttp.CreatePublicClient(TimeSpan.FromSeconds(30));
        using var llmHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        try
        {
            var signals = await TrendSignals.FetchAsync(http, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), ct);
            if (signals.IsEmpty)
            {
                Log.Warn("ideas: no trend source answered; no list today");
                return 0;
            }
            Log.Info($"ideas: {signals.Headlines.Count} headlines, {signals.Searches.Count} searches ({string.Join(", ", signals.Searches.Take(5).Select(x => x.Split(':')[0]))}...), {signals.WikipediaReads.Count} Wikipedia reads");
            var picks = await Picker(state, config, root, key, http, llmHttp).PickAsync(signals, state.TakenArticles(), ct);
            if (picks.Count == 0)
            {
                Log.Warn("ideas: no idea passed the checks; no list today");
                return 0;
            }
            var list = new IdeaList(date, DateTimeOffset.UtcNow, picks);
            var (text, buttons) = CloudFlow.IdeaListMessage(list);
            await telegram.SendTextAsync(text, buttons, ct);
            state.SaveIdeas(list);
            Log.Info($"ideas: {picks.Count} sent to Telegram");
        }
        catch (Exception ex) when (ex is LlmException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"ideas: no list today ({TextUtil.Truncate(ex.Message, 200)})");
        }
        return 0;
    }

    /// <summary>
    /// /fila and its buttons: applies the change (an item is a key from a button or a 1-based position from /mover and
    /// /remover) and sends the queue as it is now.
    /// </summary>
    private static async Task<int> QueueAsync(CloudState state, string root, string? top, string? remove, string? move, string? to,
        TelegramNotifier telegram, CancellationToken ct)
    {
        var backlog = TopicBacklog.Load(Path.Combine(root, "config", "topics.json"));
        string? done = null;
        try
        {
            if (top is not null) done = $"⏫ No topo: {Title(state.Move(backlog, top, 1).Topic)}";
            else if (remove is not null) done = $"🗑 Removido da fila: {Title(state.Remove(backlog, remove).Topic)}";
            else if (move is not null)
            {
                var position = int.TryParse(to, out var p) ? p : throw new ArgumentException("/mover needs two numbers: /mover 5 2");
                done = $"↕️ {Title(state.Move(backlog, move, position).Topic)} agora é o {position}º";
            }
        }
        catch (ArgumentException ex)
        {
            done = $"⚠️ {ex.Message} Veja os números atuais abaixo.";
        }
        var (text, buttons) = CloudFlow.QueueMessage(state.Queue(backlog), done);
        await telegram.SendTextAsync(text, buttons, ct, perRow: 2);
        return 0;

        static string Title(BacklogTopic t) => $"{t.PortugueseArticle ?? t.Article} ({t.Year})";
    }

    /// <summary>The owner tapped idea <paramref name="index"/> (1-based) of a day's list: it joins the approved ideas.</summary>
    private static int Approve(CloudState state, string date, int index)
    {
        var list = state.Ideas(date) ?? throw new ArgumentException($"No idea list for {date}.");
        if (index < 1 || index > list.Ideas.Count) throw new ArgumentException($"The list for {date} has no idea {index}.");
        state.SaveApproved(new ApprovedIdea($"{date}-{index:00}", list.Ideas[index - 1], FromText: false, DateTimeOffset.UtcNow));
        Log.Info($"ideas: approved {list.Ideas[index - 1].Article}");
        return 0;
    }

    /// <summary>The owner's text: the picker turns it into an event (one model request); it goes first in line.</summary>
    private static async Task<int> AddIdeaAsync(CloudState state, AppConfig config, string root, Func<string, string?> key, string idea,
        TelegramNotifier telegram, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idea) || idea.Length > 200) throw new ArgumentException("The idea must be 1 to 200 characters.");
        using var http = SafeHttp.CreatePublicClient(TimeSpan.FromSeconds(30));
        using var llmHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        BacklogTopic? topic = null;
        try
        {
            topic = (await Picker(state, config, root, key, http, llmHttp).PickForIdeaAsync(idea, state.TakenArticles(), ct)).FirstOrDefault();
        }
        catch (Exception ex) when (ex is LlmException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn($"idea: no topic ({TextUtil.Truncate(ex.Message, 200)})");
        }
        if (topic is null)
        {
            await telegram.SendTextAsync($"🤔 Não achei um evento na Wikipedia para \"{TextUtil.Truncate(idea.Trim(), 100)}\". Tente com outras palavras.", ct);
            return 0;
        }
        var now = DateTimeOffset.UtcNow;
        var id = $"{now.UtcDateTime:yyyyMMdd-HHmmss}-text";
        state.SaveApproved(new ApprovedIdea(id, topic, FromText: true, now));
        // The newest idea is the next video, even ahead of items the owner moved up earlier.
        state.Move(TopicBacklog.Load(Path.Combine(root, "config", "topics.json")), "a-" + id, 1);
        await telegram.SendTextAsync($"📝 Anotado: {topic.PortugueseArticle ?? topic.Article} ({topic.Year}). É o próximo vídeo da fila (veja com /fila). Para fazer agora, mande /novo {TextUtil.Truncate(idea.Trim(), 100)}", ct);
        return 0;
    }

    /// <summary>
    /// Copies the keys from this PC's environment and the two DPAPI sign-ins into the repository's Actions secrets,
    /// through gh's standard input so no value is printed or put on a command line. A new state key is made each time,
    /// so the runner starts again from the sign-ins copied here.
    /// </summary>
    private static async Task<int> SecretsAsync(AppConfig config, Func<string, string?> key, string repo)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")) throw new ArgumentException($"\"{repo}\" is not owner/name.");
        var google = key(config.YouTube.ClientIdEnvVar) is { } id && key(config.YouTube.ClientSecretEnvVar) is { } secret
            ? new OAuthClient(id, secret)
            : OAuthClient.Load(TokenStores.ExpandPath(config.YouTube.ClientSecretFile));
        var values = new List<(string Name, string? Value)>
        {
            (config.Gemini.ApiKeyEnvVar, key(config.Gemini.ApiKeyEnvVar)),
            (config.OpenRouter.ApiKeyEnvVar, key(config.OpenRouter.ApiKeyEnvVar)),
            (config.Voice.Smallest.ApiKeyEnvVar, key(config.Voice.Smallest.ApiKeyEnvVar)),
            (config.Telegram.BotTokenEnvVar, key(config.Telegram.BotTokenEnvVar)),
            (config.Telegram.ChatIdEnvVar, key(config.Telegram.ChatIdEnvVar)),
            (config.TikTok.ClientKeyEnvVar, key(config.TikTok.ClientKeyEnvVar)),
            (config.TikTok.ClientSecretEnvVar, key(config.TikTok.ClientSecretEnvVar)),
            (config.YouTube.ClientIdEnvVar, google.ClientId),
            (config.YouTube.ClientSecretEnvVar, google.ClientSecret),
            (config.YouTube.RefreshTokenEnvVar, new DpapiTokenStore(TokenStores.ExpandPath(config.YouTube.TokenFile), "youtube").Load()),
            (config.TikTok.RefreshTokenEnvVar, new DpapiTokenStore(TokenStores.ExpandPath(config.TikTok.TokenFile), "tiktok").Load()),
            (SealedFileTokenStore.KeyEnvVar, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
        };
        var failed = 0;
        foreach (var (name, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Console.WriteLine($"skipped {name}: not set on this PC");
                if (name != config.OpenRouter.ApiKeyEnvVar) failed++;
                continue;
            }
            var start = new ProcessStartInfo("gh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "secret", "set", name, "--repo", repo }) start.ArgumentList.Add(a);
            using var gh = Process.Start(start) ?? throw new IOException("gh did not start.");
            await gh.StandardInput.WriteAsync(value);
            gh.StandardInput.Close();
            var error = await gh.StandardError.ReadToEndAsync();
            await gh.WaitForExitAsync();
            // gh's own messages carry the name, never the value.
            Console.WriteLine(gh.ExitCode == 0 ? $"set {name}" : $"FAILED {name}: {error.Trim()}");
            if (gh.ExitCode != 0) failed++;
        }
        return failed == 0 ? 0 : 1;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
