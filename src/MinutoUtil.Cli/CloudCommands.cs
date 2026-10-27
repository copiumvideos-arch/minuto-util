using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MinutoUtil.Core;
using MinutoUtil.Core.Cloud;
using MinutoUtil.Core.Llm;
using MinutoUtil.Core.Publishing;

/// <summary>
/// The steps the GitHub Actions workflows (.github/workflows) run around `make` and `publish`, plus `cloud secrets`,
/// which copies this PC's keys and sign-ins into the repository's Actions secrets without printing them.
///   cloud ideas   --state <dir>                                     sends the day's 10 ideas (from the trends)
///   cloud approve --state <dir> --date <yyyyMMdd> --index <n>        the owner tapped idea n of that day's list
///   cloud add-idea --state <dir> --idea "<text>"                    the owner's own idea, first in line
///   cloud next    --state <dir> --tag <tag> [--article "<idea>"]    picks the topic, prints it as JSON
///   cloud review  --state <dir> --tag <tag> --run <folder>          sends the video to Telegram with the buttons
///   cloud publish --state <dir> --tag <tag> --run <folder>          YouTube + TikTok draft for an approved run
///   cloud reject  --state <dir> --tag <tag>
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
                    return await IdeasAsync(state, config, root, key, telegram, cts.Token);
                case "approve":
                    return Approve(state, Option(args, "--date") ?? "", int.TryParse(Option(args, "--index"), out var index) ? index : 0);
                case "add-idea":
                    return await AddIdeaAsync(state, config, root, key, Option(args, "--idea") ?? "", telegram, cts.Token);
            }
            var tag = RunTag.Validate(Option(args, "--tag"));
            var flow = new CloudFlow(state, telegram.SendTextAsync, TimeProvider.System);

            switch (step)
            {
                case "next":
                    return await NextAsync(state, tag, config, root, key, Option(args, "--article"), telegram, cts.Token);
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
                    Console.Error.WriteLine("Usage: cloud ideas|approve|add-idea|next|review|publish|reject|failed --state <dir> ..., or cloud secrets --repo <owner/name>");
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
        state.Save(new RunRecord { Tag = tag, Article = next.Article, PortugueseArticle = next.PortugueseArticle, Year = next.Year > 0 ? next.Year : null,
            Topic = next.Topic, Trend = next.Trend, Status = RunStatus.Rendering, UpdatedAt = DateTimeOffset.UtcNow });
        Console.WriteLine(JsonSerializer.Serialize(new { article = next.Article, portugueseArticle = next.PortugueseArticle ?? "", year = next.Year,
            topic = next.Topic, trend = next.Trend, left }));
        return 0;
    }

    /// <summary>
    /// The trend picker on the writer models: busy Gemini models hand over to the next, then to OpenRouter, three
    /// requests at most; Gemini's quota file lives on the state branch.
    /// </summary>
    private static TrendPicker Picker(CloudState state, AppConfig config, string root, Func<string, string?> key, HttpClient http, HttpClient llmHttp)
    {
        var usage = new RunUsage(3);
        var quota = new QuotaTracker(state.QuotaFile, sharedPaths: config.SharedQuotaFiles);
        var models = new List<ILlmClient>();
        if (key(config.Gemini.ApiKeyEnvVar) is { } gemini) models.Add(new GeminiClient(llmHttp, gemini, config.Gemini.Models, usage, quota));
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
        TelegramNotifier telegram, CancellationToken ct)
    {
        var date = IdeaList.DateFor(DateTimeOffset.UtcNow);
        if (state.Ideas(date) is not null)
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
            Log.Info($"ideas: {signals.Searches.Count} searches ({string.Join(", ", signals.Searches.Take(5).Select(x => x.Split(':')[0]))}...), {signals.WikipediaReads.Count} Wikipedia reads");
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
        state.SaveApproved(new ApprovedIdea($"{now.UtcDateTime:yyyyMMdd-HHmmss}-text", topic, FromText: true, now));
        await telegram.SendTextAsync($"📝 Anotado: {topic.PortugueseArticle ?? topic.Article} ({topic.Year}). É o próximo vídeo da fila. Para fazer agora, mande /novo {TextUtil.Truncate(idea.Trim(), 100)}", ct);
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
