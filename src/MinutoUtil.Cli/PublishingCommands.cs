using MinutoUtil.Core;
using MinutoUtil.Core.Publishing;

/// <summary>
/// youtube-login, tiktok-login and publish. Sign-ins always go to the DPAPI token files on Windows; publish reads the
/// refresh token environment variables first (the Linux runner) and the files otherwise. Tokens are never printed.
/// </summary>
internal static class PublishingCommands
{
    public static async Task<int> RunAsync(string command, string[] args, AppConfig config, Func<string, string?> key)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        using var apiHttp = SafeHttp.CreatePublicClient(TimeSpan.FromSeconds(30));
        using var uploadHttp = SafeHttp.CreatePublicClient(TimeSpan.FromMinutes(10));
        var noBrowser = args.Contains("--no-browser");
        StreamWriter? logWriter = null;
        try
        {
            if (command == "youtube-login")
            {
                var store = new DpapiTokenStore(TokenStores.ExpandPath(config.YouTube.TokenFile), "youtube", config.Channel.Id);
                WarnIfOverridden(config.YouTube.RefreshTokenEnvVar);
                var auth = new GoogleAuth(apiHttp, GoogleClient(config.YouTube, key), store, TimeProvider.System);
                string? channel = null;
                // The channel is checked before the token is saved: a sign-in with any other channel is thrown away.
                await auth.AuthorizeInteractiveAsync(
                    url => OpenSignIn(url, $"Pick the Google account, then the {config.Channel.Name} channel.", noBrowser),
                    async (token, ct) => channel = await YouTubeUploader.EnsureChannelAsync(apiHttp, token, config.YouTube.ChannelId, ct),
                    cts.Token);
                Console.WriteLine($"Signed in to YouTube channel \"{channel}\" ({config.YouTube.ChannelId}).");
                Console.WriteLine($"The refresh token is in {store.Path}, encrypted for this Windows user.");
                return 0;
            }

            if (command == "tiktok-login")
            {
                var store = new DpapiTokenStore(TokenStores.ExpandPath(config.TikTok.TokenFile), "tiktok", config.Channel.Id);
                WarnIfOverridden(config.TikTok.RefreshTokenEnvVar);
                var auth = new TikTokAuth(apiHttp, TikTokClientFrom(config.TikTok, key), store, TimeProvider.System);
                Func<string?>? paste = args.Contains("--paste")
                    ? () =>
                    {
                        Console.WriteLine("After allowing access the browser opens an address that may not load. Paste that whole address here:");
                        return Console.ReadLine();
                    }
                    : null;
                var account = await auth.AuthorizeInteractiveAsync(config.TikTok.RedirectUri,
                    url => OpenSignIn(url, $"Sign in with the {config.Channel.Name} TikTok account.", noBrowser), paste, cts.Token);
                Console.WriteLine($"Signed in to TikTok as \"{account.DisplayName}\" (scopes {account.Scopes}). Check that this is the channel's account.");
                Console.WriteLine($"The refresh token is in {store.Path}, encrypted for this Windows user.");
                return 0;
            }

            if (Option(args, "--run") is not { } runFolder)
            {
                Console.Error.WriteLine("publish needs --run <run folder>.");
                return 1;
            }
            var folder = Path.GetFullPath(runFolder);
            var dryRun = args.Contains("--dry-run");
            var force = args.Contains("--force");
            var platforms = (args.Contains("--youtube") ? Platforms.YouTube : Platforms.None) | (args.Contains("--tiktok") ? Platforms.TikTok : Platforms.None);
            if (platforms == Platforms.None) platforms = Platforms.Both;
            if (!dryRun && Directory.Exists(folder))
            {
                var writer = logWriter = new StreamWriter(Path.Combine(folder, "run.log"), append: true) { AutoFlush = true };
                Log.Sink = line =>
                {
                    Console.Error.WriteLine(line);
                    writer.WriteLine(line);
                };
                Log.Info($"publish {folder} to {platforms}{(force ? " (forced)" : "")}");
            }

            var telegram = Telegram(config.Telegram, key, apiHttp);
            var publisher = BuildPublisher(config, key, apiHttp, uploadHttp, telegram is null ? null : telegram.SendTextAsync);
            Console.WriteLine(await publisher.PublishAsync(folder, platforms, dryRun, force, cts.Token));
            return 0;
        }
        catch (Exception ex) when (ex is PublishAuthException or PublishRefusedException or YouTubeUploadException or TikTokUploadException
            or InvalidDataException or HttpRequestException or PlatformNotSupportedException)
        {
            Log.Warn($"{command} failed: {ex.Message}");
            return 1;
        }
        finally
        {
            logWriter?.Dispose();
        }
    }

    /// <summary>A YouTube access token from the stored sign-in (for /stats: videos.list with the read-only scope).</summary>
    internal static Task<string> YouTubeAccessTokenAsync(AppConfig config, Func<string, string?> key, HttpClient apiHttp, CancellationToken ct)
    {
        var auth = new GoogleAuth(apiHttp, GoogleClient(config.YouTube, key),
            TokenStores.For(config.Channel, config.YouTube.RefreshTokenEnvVar, config.YouTube.TokenFile, "youtube"), TimeProvider.System);
        if (!auth.HasSignedIn) throw new PublishAuthException($"Not signed in to YouTube. Run '{GoogleAuth.LoginCommand}' once.");
        return auth.GetAccessTokenAsync(ct);
    }

    /// <summary>
    /// The publisher with its token stores (sealed state file on the cloud runner, environment variable, or DPAPI file).
    /// The clients are made only for a real upload: a dry run needs no sign-in and touches no network.
    /// </summary>
    internal static Publisher BuildPublisher(AppConfig config, Func<string, string?> key, HttpClient apiHttp, HttpClient uploadHttp,
        Func<string, CancellationToken, Task>? sendText) =>
        new(config.YouTube, config.TikTok,
            () =>
            {
                var auth = new GoogleAuth(apiHttp, GoogleClient(config.YouTube, key),
                    TokenStores.For(config.Channel, config.YouTube.RefreshTokenEnvVar, config.YouTube.TokenFile, "youtube"), TimeProvider.System);
                if (!auth.HasSignedIn) throw new PublishAuthException($"Not signed in to YouTube. Run '{GoogleAuth.LoginCommand}' once.");
                return new YouTubeUploader(uploadHttp, auth.GetAccessTokenAsync);
            },
            () => new TikTokUploader(uploadHttp, StoredTikTokAuth(config, key, apiHttp).GetAccessTokenAsync),
            TimeProvider.System,
            sendText);

    /// <summary>TikTok with its stored sign-in (sealed state file on the cloud runner, environment variable, or DPAPI file).</summary>
    internal static TikTokAuth StoredTikTokAuth(AppConfig config, Func<string, string?> key, HttpClient apiHttp)
    {
        var auth = new TikTokAuth(apiHttp, TikTokClientFrom(config.TikTok, key),
            TokenStores.For(config.Channel, config.TikTok.RefreshTokenEnvVar, config.TikTok.TokenFile, "tiktok"), TimeProvider.System);
        if (!auth.HasSignedIn) throw new PublishAuthException($"Not signed in to TikTok. Run '{TikTokAuth.LoginCommand}' once.");
        return auth;
    }

    // AbsoluteUri keeps the escaping: ToString() turns %20 back into spaces, which breaks the link when copied.
    private static void OpenSignIn(Uri url, string hint, bool noBrowser)
    {
        Console.WriteLine($"Sign-in link: {url.AbsoluteUri}");
        Console.WriteLine(hint);
        if (noBrowser) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }

    private static void WarnIfOverridden(string envVar)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
            Log.Warn($"{envVar} is set in this shell and wins over the token file when publishing; unset it to use this sign-in");
    }

    /// <summary>The Desktop OAuth client: on a runner from two environment variables, on Windows from client_secret.json.</summary>
    private static OAuthClient GoogleClient(YouTubeOptions options, Func<string, string?> key) =>
        key(options.ClientIdEnvVar) is { } id && key(options.ClientSecretEnvVar) is { } secret
            ? new OAuthClient(id, secret)
            : OAuthClient.Load(TokenStores.ExpandPath(options.ClientSecretFile));

    /// <summary>The owner's Telegram bot, or null (with a note) when its two variables are not set.</summary>
    internal static TelegramNotifier? Telegram(TelegramOptions options, Func<string, string?> key, HttpClient http)
    {
        if (key(options.BotTokenEnvVar) is { } token && key(options.ChatIdEnvVar) is { } chat) return new TelegramNotifier(http, token, chat);
        Log.Info($"Telegram is off ({options.BotTokenEnvVar} or {options.ChatIdEnvVar} not set); the TikTok caption stays in its file");
        return null;
    }

    private static TikTokClient TikTokClientFrom(TikTokOptions options, Func<string, string?> key) =>
        key(options.ClientKeyEnvVar) is { } clientKey && key(options.ClientSecretEnvVar) is { } secret
            ? new TikTokClient(clientKey, secret)
            : throw new PublishAuthException($"Set {options.ClientKeyEnvVar} and {options.ClientSecretEnvVar} (TikTok developer portal, the app's credentials) as Windows user environment variables.");

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
