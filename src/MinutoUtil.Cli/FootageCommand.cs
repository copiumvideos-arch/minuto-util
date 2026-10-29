using MinutoUtil.Core;
using MinutoUtil.Core.Media;
using MinutoUtil.Core.Video;

/// <summary>
/// footage: downloads a game's trailers on this PC (Steam, else official YouTube channels, or the --youtube videos) and
/// puts them in the game's footage release, where the cloud runs find them. For footage YouTube refuses to the cloud.
/// The files stay in output/footage/&lt;release&gt;/ for a look before they are used.
/// </summary>
internal static class FootageCommand
{
    public static async Task<int> RunAsync(string[] args, AppConfig config, string root)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Log.Sink = Console.Error.WriteLine;
        if (Option(args, "--article") is not { Length: > 0 } game)
        {
            Console.Error.WriteLine("footage needs --article \"<the game's English Wikipedia title>\".");
            return 1;
        }
        var media = config.Media;
        if (Option(args, "--youtube") is { Length: > 0 } list)
        {
            var ids = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => (Text: v, Id: YouTubeTrailers.VideoId(v))).ToList();
            if (ids.FirstOrDefault(v => v.Id is null) is { Text: not null } bad)
            {
                Console.Error.WriteLine($"--youtube takes YouTube video ids or links, not \"{bad.Text}\".");
                return 1;
            }
            media = media with { YouTubeVideos = ids.Select(v => v.Id!).Distinct().ToList() };
        }
        if (Option(args, "--steam-app") is { Length: > 0 } steamApp)
        {
            if (!int.TryParse(steamApp, out var appId) || appId <= 0)
            {
                Console.Error.WriteLine($"--steam-app must be a Steam app id (a number), not \"{steamApp}\".");
                return 1;
            }
            media = media with { SteamAppId = appId };
        }

        var tag = FootageCache.ReleaseTag(game);
        var folder = Path.Combine(root, "output", "footage", tag);
        // Always a fresh download: the release is replaced with exactly what is fetched now.
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        using var http = SafeHttp.CreatePublicClient(TimeSpan.FromSeconds(60));
        var ffmpeg = new Ffmpeg(config.Video);
        var trailers = new GameTrailers(new SteamTrailers(http, ffmpeg, media), new YouTubeTrailers(ffmpeg, media, Path.Combine(root, "tools")), media);
        try
        {
            var found = await trailers.GatherAsync(game, Path.Combine(folder, "media"), cts.Token);
            var release = await new FootageCache(root).UploadAsync(game, found, folder, cts.Token);
            Console.WriteLine($"{found.Count} trailer(s) of {GameNames.Title(game)} in the release {release}; cloud runs of this game use them now.");
            foreach (var t in found) Console.WriteLine($"  {t.Title} ({t.Author}), {t.Width}x{t.Height}, {t.DurationSeconds:0}s");
            return 0;
        }
        catch (Exception ex) when (ex is MediaException or ToolException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
