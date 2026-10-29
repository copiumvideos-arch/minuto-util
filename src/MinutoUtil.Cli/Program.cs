using MinutoUtil.Core;
using MinutoUtil.Core.Llm;
using MinutoUtil.Core.Media;
using MinutoUtil.Core.Scripts;
using MinutoUtil.Core.Sources;
using MinutoUtil.Core.Video;
using MinutoUtil.Core.Voice;

// minuto-util make [--run <folder>] [--article "<English Wikipedia title>" [--year <yyyy>]] [--topic <series>] [--steam-app <id>] [--quota-file <path>]
//   Makes one video into output/<yyyy-MM-dd>/<HHmm>/, or finishes/re-renders an existing run folder, reusing
//   everything already in it (sources, script, voice takes, media, shots). Nothing is uploaded.
// minuto-util publish --run <folder> [--youtube] [--tiktok] [--dry-run] [--force]
//   Sends a finished run to YouTube (public Short) and to the owner's TikTok inbox (draft); see PublishingCommands.cs.
// minuto-util cover --run <folder> [--text ..] [--scene ..] [--date ..] [--youtube] [--telegram] [--caption "<text>"]
//   A new painted thumbnail for a run, set on its published Short; see CoverCommand.cs.
// minuto-util cloud next|review|publish|reject|failed|secrets ...
//   The steps of the GitHub Actions runs; see CloudCommands.cs.
// Every command takes --channel <id> (default minuto-util): the channel in channels/<id>/ whose config, topics,
// prompts, sign-ins, secrets and state it uses.

var command = args.FirstOrDefault() ?? "help";
Channel? channel = null;
if (command is not ("help" or "--help" or "-h") && !TryLoadChannel(out channel))
    return 1;
if (command is "check-script" && Option("--run") is { } checkFolder)
{
    // Shows what the local fixes (dashes, accents) and checks make of a run's saved script, without changing it or
    // calling any service. Useful before deleting voice takes to re-voice a script.
    var saved = MinutoUtil.Core.Json.Options;
    var sourcesSaved = System.Text.Json.JsonSerializer.Deserialize<List<SourceArticle>>(File.ReadAllText(Path.Combine(checkFolder, "sources.json")), saved)!;
    var scriptSaved = System.Text.Json.JsonSerializer.Deserialize<VideoScript>(File.ReadAllText(Path.Combine(checkFolder, "script.json")), saved)!;
    var polishedScript = ScriptWriter.Polish(scriptSaved, sourcesSaved);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(polishedScript, saved));
    foreach (var warning in new ScriptWriter(new ChainedLlmClient([]), "", channel!.Config.Writer, "").Check(polishedScript, sourcesSaved))
        Console.WriteLine($"warning: {warning}");
    return 0;
}
if (command is "cloud")
    return await CloudCommands.RunAsync(args, channel!, FindRepositoryRoot(), Key);
if (command is "cover")
    return await CoverCommand.RunAsync(args, channel!.Config, FindRepositoryRoot(), Key);
if (command is "youtube-login" or "tiktok-login" or "publish")
    return await PublishingCommands.RunAsync(command, args, channel!.Config, Key);
if (command is not "make")
{
    Console.WriteLine("""
        Every command takes --channel <id> (default minuto-util), one of the folders in channels/.

        Usage: minuto-util make [--run <run folder>] [--article "<English Wikipedia title>" [--year <yyyy>]] [--topic <series>]
               minuto-util check-script --run <run folder>
               minuto-util youtube-login [--no-browser]
               minuto-util tiktok-login [--no-browser] [--paste]
               minuto-util publish --run <run folder> [--youtube] [--tiktok] [--dry-run] [--force]
               minuto-util cover --run <run folder> [--text "<words>"] [--scene "<scene>"] [--date "<date>"] [--youtube] [--telegram] [--caption "<text>"]
               minuto-util cloud secrets --repo <owner/name>

          make          Writes, voices and renders one video into output/<yyyy-MM-dd>/<HHmm>/.
          --run         Re-render an existing run folder, reusing its script, voice takes and media (no new API calls).
          --article     The event's English Wikipedia article (default: story.englishArticle in channels/<id>/config.json).
          --portuguese-article  The Portuguese Wikipedia article to use with it (default: the English page's link).
          --year        The year of that event (pictures from it rank higher, and the year card shows it).
          --topic       The series name given to the writer (default: story.topic).
          --steam-app   A trailer channel: the game's Steam app id, when the article's name does not find it.
          --script-only Stop after writing the script (a later make --run of the folder goes on from there).
          check-script  Prints a run's script after the local fixes and checks; changes nothing, calls nothing.
          youtube-login Signs in to YouTube once (browser); only the channel's youtube.channelId is accepted.
          tiktok-login  Signs in to TikTok once (browser). --paste: paste the address the browser ends on instead.
          publish       Uploads a run's video.mp4: a public YouTube Short and a TikTok inbox draft (both unless one
                        is named). --dry-run prints what would be sent and calls nothing. --force posts a folder again.
          cover         Paints a new thumbnail for a run (words, scene and date from its script.json or the options);
                        --youtube sets it on the published Short, --telegram sends it to the owner (with --caption, if given).
          cloud secrets Copies this PC's keys and sign-ins into the GitHub repository's Actions secrets (never printed).
        """);
    return command is "help" or "--help" or "-h" ? 0 : 1;
}

var root = FindRepositoryRoot();
var config = channel!.Config;
// Another article is another event: its year comes with --year or not at all (the config's year is the default article's).
if (Option("--article") is { } article)
    config = config with { Story = config.Story with { EnglishArticle = article, PortugueseArticle = "", EventYear = int.TryParse(Option("--year"), out var year) && year != 0 ? year : null } };
if (Option("--portuguese-article") is { Length: > 0 } portuguese) config = config with { Story = config.Story with { PortugueseArticle = portuguese } };
if (Option("--topic") is { Length: > 0 } topic) config = config with { Story = config.Story with { Topic = topic } };
if (Option("--steam-app") is { Length: > 0 } steamApp)
{
    if (!int.TryParse(steamApp, out var steamAppId) || steamAppId <= 0)
    {
        Console.Error.WriteLine($"--steam-app must be a Steam app id (a number), not \"{steamApp}\".");
        return 1;
    }
    config = config with { Media = config.Media with { SteamAppId = steamAppId } };
}

var now = DateTime.Now;
var folder = Option("--run") is { } run ? Path.GetFullPath(run) : Path.Combine(root, "output", now.ToString("yyyy-MM-dd"), now.ToString("HHmm"));
Directory.CreateDirectory(folder);
var logPath = Path.Combine(folder, "run.log");
var logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
Log.Sink = line =>
{
    Console.Error.WriteLine(line);
    logWriter.WriteLine(line);
};
Log.Info($"run {folder} started ({(Option("--run") is null ? "new" : "resumed")})");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var usage = new RunUsage(config.Writer.MaxRequests, config.Writer.MaxSpareRequests);
using var http = SafeHttp.CreatePublicClient(TimeSpan.FromSeconds(60));
using var apiHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
// The cloud runs keep the quota file on the state branch (--quota-file), so it carries over between jobs.
var quota = new QuotaTracker(Path.GetFullPath(Option("--quota-file") ?? Path.Combine(root, "data", "quota-state.json")), sharedPaths: config.SharedQuotaFiles);

var writers = new List<ILlmClient>();
if (Key(config.Gemini.ApiKeyEnvVar) is { } geminiKey) writers.Add(new GeminiClient(apiHttp, geminiKey, config.Gemini.Models, usage, quota));
else Log.Warn($"{config.Gemini.ApiKeyEnvVar} is not set; Gemini is skipped");
if (Key(config.CloudflareAi.TokenEnvVar) is { } cloudflareToken && Key(config.CloudflareAi.AccountIdEnvVar) is { } cloudflareAccount)
    writers.Add(OpenRouterClient.ForCloudflare(apiHttp, cloudflareToken, cloudflareAccount, config.CloudflareAi.Models, usage, quota));
if (Key(config.OpenRouter.ApiKeyEnvVar) is { } openRouterKey) writers.Add(new OpenRouterClient(apiHttp, openRouterKey, config.OpenRouter.WriterModels, usage, quota));
var prompt = File.ReadAllText(channel.WriterPromptFile);

var tools = Path.Combine(root, "tools");
var smallest = Key(config.Voice.Smallest.ApiKeyEnvVar) is { } smallestKey ? new SmallestVoice(apiHttp, smallestKey, config.Voice.Smallest, usage) : null;
var narrator = new Narrator(config.Voice, tools, smallest, new KokoroVoice(tools, config.Voice), usage);

var ffmpeg = new Ffmpeg(config.Video);
var imageSources = new List<IMediaSource>();
if (config.Media.Commons) imageSources.Add(new CommonsSource(http, config.Media));
if (config.Media.LibraryOfCongress) imageSources.Add(new LibraryOfCongressSource(http, config.Media));
var videoSources = new List<IMediaSource>();
if (config.Media.InternetArchive) videoSources.Add(new InternetArchiveSource(http, config.Media));
if (config.Media.Commons) videoSources.Add(new CommonsSource(http, config.Media));
var gatherer = new MediaGatherer(imageSources, videoSources, new MediaDownloader(http, ffmpeg, config.Media),
    new ClipExtractor(ffmpeg, config.Video), config.Media, config.Story.EventYear, new CommonsSource(http, config.Media));

// The painted thumbnail picture (style.thumbnailPicture "ai") uses the same free Cloudflare account as the spare writer.
ICoverPainter? painter = Key(config.CloudflareAi.TokenEnvVar) is { } paintToken && Key(config.CloudflareAi.AccountIdEnvVar) is { } paintAccount
    ? new CloudflareCoverPainter(apiHttp, paintToken, paintAccount, config.CloudflareAi, usage)
    : null;
// The game's official trailers, for a channel whose footage is trailers (media.footage).
var trailers = config.Media.Footage == "trailers" ? new SteamTrailers(http, ffmpeg, config.Media) : null;
var pipeline = new Pipeline(config, root, new WikipediaClient(http),
    () => new ScriptWriter(new ChainedLlmClient(writers), prompt, config.Writer, config.Channel.Name), narrator, gatherer, painter, trailers);
try
{
    var result = await pipeline.RunAsync(folder, cts.Token, scriptOnly: args.Contains("--script-only"));
    if (result is null)
    {
        Log.Info($"script only: {Path.Combine(folder, "script.json")}");
        return 0;
    }
    Log.Info($"done: {result.VideoPath} ({result.Duration:0.0}s, voice {result.Voice}, {result.Lufs:0.0} LUFS)");
    Log.Info($"contact sheet: {result.ContactSheetPath}");
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Warn($"run failed: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
finally
{
    Log.Info(usage.Summary());
    logWriter.Dispose();
}

// The channel named by --channel (default minuto-util); a bad id or a missing folder is reported, not thrown. The last
// --channel counts: the workflows append it after the other options, some of which carry the owner's text.
bool TryLoadChannel(out Channel? loaded)
{
    try
    {
        var at = Array.LastIndexOf(args, "--channel");
        loaded = Channel.Load(FindRepositoryRoot(), at < 0 ? Channel.DefaultId : at + 1 < args.Length ? args[at + 1] : "");
        return true;
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine(ex.Message);
        loaded = null;
        return false;
    }
}

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

// A shell started before the keys were set does not see them; on Windows the user environment is read directly.
static string? Key(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
        value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

static string FindRepositoryRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MinutoUtil.slnx"))) return dir.FullName;
    throw new DirectoryNotFoundException("Run minuto-util from inside the minuto-util repository (MinutoUtil.slnx not found).");
}
