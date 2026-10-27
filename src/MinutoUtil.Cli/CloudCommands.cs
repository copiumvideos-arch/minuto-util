using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MinutoUtil.Core;
using MinutoUtil.Core.Cloud;
using MinutoUtil.Core.Publishing;

/// <summary>
/// The steps the GitHub Actions workflows (.github/workflows) run around `make` and `publish`, plus `cloud secrets`,
/// which copies this PC's keys and sign-ins into the repository's Actions secrets without printing them.
///   cloud next    --state <dir> --tag <tag> [--article "<title>"]   picks the topic, prints it as JSON
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
            var tag = RunTag.Validate(Option(args, "--tag"));
            using var apiHttp = SafeHttp.CreatePublicClient(TimeSpan.FromSeconds(30));
            using var uploadHttp = SafeHttp.CreatePublicClient(TimeSpan.FromMinutes(10));
            var telegram = PublishingCommands.Telegram(config.Telegram, key, apiHttp)
                ?? throw new ArgumentException($"The cloud runs report to Telegram: set {config.Telegram.BotTokenEnvVar} and {config.Telegram.ChatIdEnvVar}.");
            var flow = new CloudFlow(state, telegram.SendTextAsync, TimeProvider.System);

            switch (step)
            {
                case "next":
                    return await NextAsync(state, tag, Path.Combine(root, "config", "topics.json"), Option(args, "--article"), telegram, cts.Token);
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
                    Console.Error.WriteLine("Usage: cloud next|review|publish|reject|failed --state <dir> --tag <tag> ..., or cloud secrets --repo <owner/name>");
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
    /// Picks the next backlog topic (or the given article), records the run as rendering and prints
    /// {"article","year","topic","left"} for the workflow. Exit code 3 when the backlog is empty.
    /// </summary>
    private static async Task<int> NextAsync(CloudState state, string tag, string backlogPath, string? article, TelegramNotifier telegram, CancellationToken ct)
    {
        var backlog = TopicBacklog.Load(backlogPath);
        var (next, left) = state.NextTopic(backlog);
        if (!string.IsNullOrWhiteSpace(article))
            next = backlog.Topics.FirstOrDefault(t => string.Equals(t.Article, article.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? new BacklogTopic(article.Trim(), 0, "História");
        if (next is null)
        {
            await telegram.SendTextAsync("📭 A fila de temas acabou (config/topics.json). Adicione eventos para continuar.", ct);
            return 3;
        }
        if (string.IsNullOrWhiteSpace(article) && left <= 5)
            await telegram.SendTextAsync($"📋 Restam {left} temas na fila (config/topics.json).", ct);
        state.Save(new RunRecord { Tag = tag, Article = next.Article, Year = next.Year > 0 ? next.Year : null, Topic = next.Topic,
            Status = RunStatus.Rendering, UpdatedAt = DateTimeOffset.UtcNow });
        Console.WriteLine(JsonSerializer.Serialize(new { article = next.Article, year = next.Year, topic = next.Topic, left }));
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
