using System.Text.Json;
using MinutoUtil.Core;
using MinutoUtil.Core.Publishing;
using MinutoUtil.Core.Scripts;
using MinutoUtil.Core.Video;

/// <summary>
/// cover: a new painted thumbnail for a run folder, for videos made before the painted cover existed. The words, the
/// scene and the date come from the run's script.json or from --text, --scene and --date (a published run's release
/// has no script). One image request, reused on a second run; --youtube sets it on the published Short (50 quota
/// units), --telegram sends it to the owner.
/// </summary>
internal static class CoverCommand
{
    public static async Task<int> RunAsync(string[] args, AppConfig config, string root, Func<string, string?> key)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        if (Option(args, "--run") is not { } runFolder || !Directory.Exists(runFolder))
        {
            Console.Error.WriteLine("cover needs --run <existing run folder>.");
            return 1;
        }
        var folder = Path.GetFullPath(runFolder);
        using var logWriter = new StreamWriter(Path.Combine(folder, "run.log"), append: true) { AutoFlush = true };
        Log.Sink = line =>
        {
            Console.Error.WriteLine(line);
            logWriter.WriteLine(line);
        };

        var scriptPath = Path.Combine(folder, "script.json");
        var script = File.Exists(scriptPath) ? JsonSerializer.Deserialize<VideoScript>(await File.ReadAllTextAsync(scriptPath), Json.Options) : null;
        var metadataPath = Path.Combine(folder, "metadata.json");
        var title = File.Exists(metadataPath) ? JsonSerializer.Deserialize<VideoMetadata>(await File.ReadAllTextAsync(metadataPath), Json.Options)?.Title : null;
        var text = Option(args, "--text") ?? NonEmpty(script?.ThumbnailText) ?? title;
        var scene = Option(args, "--scene") ?? NonEmpty(script?.ThumbnailScene);
        var date = Option(args, "--date") ?? script?.EraStamp;
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(scene))
        {
            Console.Error.WriteLine("cover needs the words and the scene: a script.json with thumbnailText and thumbnailScene, or --text and --scene.");
            return 1;
        }
        if (key(config.CloudflareAi.TokenEnvVar) is not { } token || key(config.CloudflareAi.AccountIdEnvVar) is not { } account)
        {
            Console.Error.WriteLine($"cover needs {config.CloudflareAi.TokenEnvVar} and {config.CloudflareAi.AccountIdEnvVar} (Cloudflare Workers AI).");
            return 1;
        }

        using var apiHttp = SafeHttp.CreatePublicClient(TimeSpan.FromMinutes(3));
        try
        {
            var painter = new CloudflareCoverPainter(apiHttp, token, account, config.CloudflareAi);
            var painted = await CoverArtFile.PaintOrReuseAsync(folder, scene, config.CloudflareAi, painter, cts.Token);
            if (painted is null) return 1;

            var fonts = Path.Combine(folder, "fonts");
            Directory.CreateDirectory(fonts);
            foreach (var file in new[] { config.Video.FontFile, config.Video.StampFontFile }.Distinct())
                File.Copy(Path.Combine(root, file), Path.Combine(fonts, Path.GetFileName(file)), overwrite: true);
            var emblem = config.Video.Style.Watermark && Path.Combine(root, config.Video.Style.WatermarkImage) is var e && File.Exists(e) ? e : null;
            var thumbnail = Path.Combine(folder, Thumbnail.FileName);
            await Thumbnail.RenderAsync(config.Video, painted.Path, text, date, folder, emblem, thumbnail, cts.Token);

            string? youtubeResult = null;
            if (args.Contains("--youtube"))
            {
                var publisher = PublishingCommands.BuildPublisher(config, key, apiHttp, apiHttp, null);
                youtubeResult = await publisher.ReplaceThumbnailAsync(folder, cts.Token);
                Console.WriteLine($"YouTube thumbnail: {youtubeResult}");
            }
            if (args.Contains("--telegram") && PublishingCommands.Telegram(config.Telegram, key, apiHttp) is { } telegram)
            {
                var caption = (Option(args, "--caption") ?? $"🖼️ Nova capa: {title ?? text}") + youtubeResult switch
                {
                    null => "",
                    "set" => "\nNo YouTube ela já está no vídeo.",
                    _ => $"\n⚠️ Não foi para o YouTube ({youtubeResult}).",
                };
                await telegram.SendPhotoAsync(thumbnail, caption, cts.Token);
            }
            Console.WriteLine(thumbnail);
            return youtubeResult is null or "set" ? 0 : 2;
        }
        catch (Exception ex) when (ex is CoverArtException or ToolException or PublishAuthException or PublishRefusedException or HttpRequestException)
        {
            Log.Warn($"cover failed: {ex.Message}");
            return 1;
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
