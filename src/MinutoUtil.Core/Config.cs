using System.Text.Json;

namespace MinutoUtil.Core;

/// <summary>
/// Everything a run can be tuned with. A channel's channels/&lt;id&gt;/config.json overrides these defaults; load it with
/// <see cref="Channel.Load"/>, which also fills the names derived from the channel (environment variables, token files).
/// </summary>
public sealed record AppConfig
{
    public ChannelOptions Channel { get; init; } = new();
    public StoryOptions Story { get; init; } = new();
    public GeminiOptions Gemini { get; init; } = new();
    public CloudflareAiOptions CloudflareAi { get; init; } = new();
    public OpenRouterOptions OpenRouter { get; init; } = new();
    public WriterOptions Writer { get; init; } = new();
    public VoiceOptions Voice { get; init; } = new();
    public MediaOptions Media { get; init; } = new();
    public VideoOptions Video { get; init; } = new();
    public YouTubeOptions YouTube { get; init; } = new();
    public TikTokOptions TikTok { get; init; } = new();
    public Publishing.TelegramOptions Telegram { get; init; } = new();

    /// <summary>
    /// Quota files of other apps that use the same API keys, read only. The Gemini key is shared with the live
    /// Flopômetro channel; its D:\projects\canal-dark\data\quota-state.json says which models are out today, so this
    /// app skips them without spending a request. A missing file is ignored.
    /// </summary>
    public IReadOnlyList<string> SharedQuotaFiles { get; init; } = [];

    /// <summary>The file as written, without the names derived from the channel; use <see cref="Channel.Load"/>.</summary>
    internal static AppConfig Load(string path) =>
        JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json.Options)
        ?? throw new InvalidDataException($"{path} is empty.");
}

/// <summary>
/// Who the channel is. Everything that must never mix between two channels (sign-ins, secrets, state, schedule) is
/// named from here, so each channel's config.json sets these itself; there are no shared defaults for them.
/// </summary>
public sealed record ChannelOptions
{
    /// <summary>The folder name under channels/; set by <see cref="Channel.Load"/>, not written in config.json.</summary>
    public string Id { get; init; } = "";

    /// <summary>The channel's name: watermark text, Telegram status header, sign-in hints.</summary>
    public string Name { get; init; } = "";

    /// <summary>Wikipedia language code of the narration; English is always fetched too.</summary>
    public string Language { get; init; } = "pt";

    /// <summary>
    /// Start of every secret's environment variable and Actions secret name: MINUTO gives MINUTO_TELEGRAM_BOT_TOKEN,
    /// MINUTO_YOUTUBE_REFRESH_TOKEN, MINUTO_STATE_KEY and so on. Upper case letters and digits.
    /// </summary>
    public string EnvPrefix { get; init; } = "";

    /// <summary>The folder of this PC's DPAPI sign-in files and the Google client file (%LOCALAPPDATA% is expanded).</summary>
    public string TokenFolder { get; init; } = "";

    /// <summary>
    /// The channel's folder inside the state branch checkout: "." (the root) only for Minuto Útil, whose files were
    /// there before there were channels; channels/&lt;id&gt; for every other channel, so no two channels share a file.
    /// </summary>
    public string StateFolder { get; init; } = "";

    /// <summary>
    /// GitHub release names of the runs are this plus the run tag: "run-" for Minuto Útil, run-&lt;id&gt;- for the others,
    /// so two channels rendering in the same minute never share a release.
    /// </summary>
    public string ReleasePrefix { get; init; } = "";

    /// <summary>The scheduled renders in UTC hours; render.yml has one cron line per hour (a test checks it).</summary>
    public IReadOnlyList<int> ScheduleUtcHours { get; init; } = [];

    /// <summary>The daily idea list in UTC hours (ideas.yml); empty for a channel without one.</summary>
    public IReadOnlyList<int> IdeasUtcHours { get; init; } = [];

    /// <summary>The 32-byte key of the sealed token files on the state branch (an Actions secret).</summary>
    public string StateKeyEnvVar => $"{EnvPrefix}_STATE_KEY";

    /// <summary>Set on the runner to the channel's state folder: the sealed token files are kept there.</summary>
    public string StateDirEnvVar => $"{EnvPrefix}_STATE_DIR";
}

/// <summary>The event the video is about: an English Wikipedia article; its Portuguese twin is found by language link.</summary>
public sealed record StoryOptions
{
    public string Topic { get; init; } = "Segunda Guerra Mundial";
    public string EnglishArticle { get; init; } = "Normandy landings";

    /// <summary>Empty: taken from the English article's language link.</summary>
    public string PortugueseArticle { get; init; } = "";

    /// <summary>When the event happened: pictures from that year rank higher, photos from decades later (memorials) lower.</summary>
    public int? EventYear { get; init; } = 1944;

    /// <summary>Characters of each article given to the writer. The intro and the first sections carry the key facts.</summary>
    public int MaxSourceChars { get; init; } = 16000;
}

public sealed record GeminiOptions
{
    /// <summary>The same text models as the Flopômetro config, tried in order, one request each at most.</summary>
    public IReadOnlyList<string> Models { get; init; } = ["gemini-3.8-flash", "gemini-3.7-flash", "gemini-3.5-flash"];
    public string ApiKeyEnvVar { get; init; } = "GEMINI_API_KEY";
}

/// <summary>
/// Cloudflare Workers AI, the first spare after Gemini: open models on the owner's free Cloudflare account. The token
/// (permission Workers AI) and the account id come from the environment (Actions secrets).
/// </summary>
public sealed record CloudflareAiOptions
{
    public IReadOnlyList<string> Models { get; init; } = ["@cf/openai/gpt-oss-120b", "@cf/meta/llama-3.3-70b-instruct-fp8-fast"];
    public string TokenEnvVar { get; init; } = "CLOUDFLARE_AI_TOKEN";
    public string AccountIdEnvVar { get; init; } = "CLOUDFLARE_ACCOUNT_ID";

    /// <summary>
    /// The image model for the painted thumbnail (style.thumbnailPicture "ai"). FLUX.2 klein 4B takes a size and costs
    /// about 160 of the 10,000 free daily neurons at 864x1536.
    /// </summary>
    public string ImageModel { get; init; } = "@cf/black-forest-labs/flux-2-klein-4b";

    /// <summary>9:16 and multiples of 16; the thumbnail scales it up to 1080x1920.</summary>
    public int ImageWidth { get; init; } = 864;
    public int ImageHeight { get; init; } = 1536;

    /// <summary>
    /// Put before every scene, so each cover looks like the same series whatever the story: a painting, never a fake
    /// photo, with room in the middle for the text.
    /// </summary>
    public string ImageStyle { get; init; } =
        "Vintage oil painting in the style of a nineteenth century history painting, visible brush strokes, muted earthy " +
        "colors with deep shadows and warm dramatic light, tall vertical composition with one clear subject in the middle. " +
        "No text, no letters, no logos, no modern objects, no close-up faces.";
}

public sealed record OpenRouterOptions
{
    /// <summary>Free writers when every Gemini model is out of quota (the Flopômetro writer list, checked 2026-10-27).</summary>
    public IReadOnlyList<string> WriterModels { get; init; } = ["nvidia/nemotron-3.5-super-vl:free", "thinkingmachines/inkling:free"];
    public string ApiKeyEnvVar { get; init; } = "OPENROUTER_API_KEY";
}

public sealed record WriterOptions
{
    /// <summary>
    /// Gemini requests a whole run may make. The Gemini key's free quota is shared with a live channel, so a failing
    /// model is never retried in a loop: each model is asked once and the budget stops the rest.
    /// </summary>
    public int MaxRequests { get; init; } = 3;

    /// <summary>
    /// Requests a whole run may make to the spare writers (Cloudflare Workers AI, then OpenRouter), counted apart from
    /// Gemini so that failed Gemini requests never use up their turn. Enough to ask each spare model once, and a few more
    /// for the rewrite of a script far outside the word range.
    /// </summary>
    public int MaxSpareRequests { get; init; } = 6;

    /// <summary>Spoken words of hook plus sections. pt-BR narration runs at about 150 words a minute at speed 1.0.</summary>
    public int MinWords { get; init; } = 130;
    public int MaxWords { get; init; } = 170;
    public int MinSections { get; init; } = 4;
    public int MaxSections { get; init; } = 6;

    /// <summary>A script this far outside the word range is asked for again (one more request); closer, it is used with a warning.</summary>
    public int HardWordMargin { get; init; } = 25;
}

public sealed record VoiceOptions
{
    /// <summary>"smallest" (Smallest.ai, falling back to Kokoro) or "kokoro" (local only).</summary>
    public string Engine { get; init; } = "smallest";
    public SmallestOptions Smallest { get; init; } = new();
    public string KokoroVoice { get; init; } = "pm_santa";
    public double KokoroSpeed { get; init; } = 1.0;

    /// <summary>Silence between sections, and the longest pause kept inside one.</summary>
    public double SectionPause { get; init; } = 0.4;
    public double MaxPause { get; init; } = 0.55;

    /// <summary>"whisper" times the words; "none" spreads them evenly over each take (tests, where the voice is a tone).</summary>
    public string Listener { get; init; } = "whisper";

    /// <summary>Below this share of script words heard back by Whisper, the section is logged as suspect.</summary>
    public double MinAlignScore { get; init; } = 0.8;
}

public sealed record SmallestOptions
{
    public string Endpoint { get; init; } = "https://api.smallest.ai/waves/v1/tts";
    public string VoiceId { get; init; } = "thiago";
    public string Model { get; init; } = "lightning_v3.1_pro";
    public string Language { get; init; } = "pt";
    public int SampleRate { get; init; } = 24000;
    public double Speed { get; init; } = 1.0;
    public string ApiKeyEnvVar { get; init; } = "SMALLEST_API_KEY";
}

public sealed record MediaOptions
{
    public bool Commons { get; init; } = true;
    public bool LibraryOfCongress { get; init; } = true;
    public bool InternetArchive { get; init; } = true;

    /// <summary>Images whose shorter side is below this look soft once cropped to 9:16 and are skipped.</summary>
    public int MinImageShortSide { get; init; } = 760;

    /// <summary>Downloads below this are thumbnails, icons or error pages.</summary>
    public int MinImageBytes { get; init; } = 40_000;
    public int MaxDownloadBytes { get; init; } = 40_000_000;

    /// <summary>Candidates fetched per search query.</summary>
    public int ResultsPerQuery { get; init; } = 12;

    /// <summary>Length of the real footage clip, no audio.</summary>
    public double ClipSeconds { get; init; } = 4.5;

    /// <summary>Title words that point at graphic images (dead or wounded people); such files are never used.</summary>
    public IReadOnlyList<string> BlockedWords { get; init; } =
    [
        "dead", "death", "corpse", "corpses", "body", "bodies", "killed", "casualty", "casualties", "wounded", "injured",
        "cadaver", "grave", "graves", "burial", "execution", "executed", "massacre", "atrocity", "mortos", "morto",
        "cadaveres", "feridos", "tote", "leichen", "gefallene",
    ];

    /// <summary>Title words of files that are rarely good full-screen pictures (maps, logos, diagrams).</summary>
    public IReadOnlyList<string> PenalizedWords { get; init; } =
        ["map", "mapa", "karte", "diagram", "chart", "logo", "flag", "insignia", "patch", "badge", "stamp", "coin", "poster", "plan", "svg", "memorial", "museum", "cemetery", "monument",
         "satellite", "landsat", "sentinel", "nasa", "reenactment", "reenactors", "handbook", "page", "uniforms",
         "illustration", "drawing", "scan", "document"];
}

public sealed record VideoOptions
{
    public int Width { get; init; } = 1080;
    public int Height { get; init; } = 1920;
    public int Fps { get; init; } = 30;

    /// <summary>Cross-fade between shots.</summary>
    public double FadeSeconds { get; init; } = 0.35;

    /// <summary>A section longer than this gets two shots instead of one.</summary>
    public double MaxShotSeconds { get; init; } = 6.5;

    /// <summary>Silence after the last word, so the video does not cut off mid-breath.</summary>
    public double TailSeconds { get; init; } = 0.6;
    public double VoiceLufs { get; init; } = -16;

    /// <summary>Internal render scale of the still-image motion: 2 renders at 2160x3840 and scales down, so slow moves do not shimmer.</summary>
    public double MotionSupersample { get; init; } = 2.0;

    /// <summary>Ken Burns zoom over one shot (0.10 = 10%).</summary>
    public double KenBurnsZoom { get; init; } = 0.10;

    /// <summary>Parallax: how far the nearest pixels travel over one shot, as a share of the frame width.</summary>
    public double ParallaxTravel { get; init; } = 0.045;

    /// <summary>Captions, the year and the map: Roboto Serif Black (OFL), a static instance named "Roboto Serif Black".</summary>
    public string FontFile { get; init; } = "assets/fonts/RobotoSerif-Black.ttf";
    public string FontName { get; init; } = "Roboto Serif Black";
    /// <summary>Date stamp, place line and watermark text: Roboto Serif ExtraBold.</summary>
    public string StampFontFile { get; init; } = "assets/fonts/RobotoSerif-ExtraBold.ttf";
    public string StampFontName { get; init; } = "Roboto Serif ExtraBold";
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string FfprobePath { get; init; } = "ffprobe";
    public string Encoder { get; init; } = "libx264";

    /// <summary>Style layers over the edit; each can be switched off.</summary>
    public StyleOptions Style { get; init; } = new();

    /// <summary>Peak video bitrate. 4500 kbit/s keeps a 75 s video near 43 MB, under Telegram's 50 MB bot upload limit.</summary>
    public int MaxVideoKbps { get; init; } = 4500;

    /// <summary>
    /// The largest file a render may make, in MB (10^6 bytes), with room under the 50 MB a Telegram bot can send.
    /// Longer videos get a lower bitrate cap than <see cref="MaxVideoKbps"/> so they still fit.
    /// </summary>
    public double MaxFileMegabytes { get; init; } = 45;
}

/// <summary>
/// The look of a video, one switch per layer. No music and no sound effects: the owner finds them distracting.
/// </summary>
public sealed record StyleOptions
{
    /// <summary>Warm faded grade, soft vignette, fine grain and a faint flicker over the picture (not the captions).</summary>
    public bool Vintage { get; init; } = true;

    /// <summary>Grain strength (FFmpeg noise, luma only, changing every frame); 0 for none.</summary>
    public int Grain { get; init; } = 7;

    /// <summary>Vignette angle in radians (FFmpeg vignette): larger is darker edges. 0.6 is clearly visible, not heavy.</summary>
    public double Vignette { get; init; } = 0.6;

    /// <summary>Warmth of the mid tones (a small push to red, away from blue). Kept low so black-and-white stays neutral.</summary>
    public double Warmth { get; init; } = 0.035;

    /// <summary>Sharpening strength (FFmpeg unsharp luma amount); 0 for none. Applied before the grain, after a light denoise.</summary>
    public double Sharpen { get; init; } = 0.7;

    /// <summary>A light denoise before sharpening, so film grain and JPEG noise in old scans are not sharpened too.</summary>
    public bool Denoise { get; init; } = true;

    /// <summary>Contrast and saturation (1 = unchanged).</summary>
    public double Contrast { get; init; } = 1.12;
    public double Saturation { get; init; } = 1.18;

    /// <summary>A short warm light leak on two cross-fades (needs <see cref="Vintage"/>).</summary>
    public bool LightLeaks { get; init; } = true;

    /// <summary>The year rolling up like a counter over the opening footage, with the place under it; replaces the date stamp.</summary>
    public bool YearCard { get; init; } = true;
    public double YearCardSeconds { get; init; } = 1.8;

    /// <summary>A short map shot after the hook, zooming from Europe into the place (Natural Earth, public domain).</summary>
    public bool MapInsert { get; init; } = true;
    public double MapSeconds { get; init; } = 2.6;

    /// <summary>Where the Natural Earth countries file is kept once downloaded (untrusted data, validated on load).</summary>
    public string MapDataDirectory { get; init; } = "assets/cache/naturalearth";

    /// <summary>The channel emblem and the channel's name, small and semi-transparent, top left.</summary>
    public bool Watermark { get; init; } = true;

    /// <summary>The words next to the emblem; <see cref="Channel.Load"/> fills in the channel's name when empty.</summary>
    public string WatermarkText { get; init; } = "";

    /// <summary>
    /// The emblem drawn as the watermark: a small round PNG with transparency, made from assets/brand/emblem.png by
    /// tools/watermark.py. When the file is missing, a vector hourglass is drawn instead.
    /// </summary>
    public string WatermarkImage { get; init; } = "assets/brand/watermark.png";

    /// <summary>
    /// thumbnail.jpg next to the video: the first still with the same grade, the year, the script's thumbnail text and
    /// the emblem. Set as the YouTube thumbnail and sent on Telegram for the TikTok cover.
    /// </summary>
    public bool Thumbnail { get; init; } = true;

    /// <summary>
    /// The picture behind the thumbnail text: "archive" (the first still of the video) or "ai" (a painting of the
    /// script's thumbnailScene from Cloudflare Workers AI, one request per video; the archive picture when it fails).
    /// </summary>
    public string ThumbnailPicture { get; init; } = "archive";

    /// <summary>
    /// Frames at the very start of the video replaced by the thumbnail (needs <see cref="Thumbnail"/>), so the owner
    /// picks the cover in TikTok's cover picker by leaving it at the start. 1 is a 1/30 s flash; 0 for none.
    /// </summary>
    public int CoverFrames { get; init; } = 1;

    /// <summary>Caption words appear one by one as they are spoken, each with a quick pop.</summary>
    public bool WordPop { get; init; } = true;

    /// <summary>Names, dates, numbers and places in the highlight color; the rest in the text color.</summary>
    public bool KeyWords { get; init; } = true;

    /// <summary>Caption, place line and watermark text, as #RRGGBB: the brand's cream.</summary>
    public string TextColor { get; init; } = "#F7E2C7";

    /// <summary>Key words, the year, the map pin and the fallback hourglass, as #RRGGBB: the brand's red.</summary>
    public string HighlightColor { get; init; } = "#DC010A";
}

/// <summary>
/// The YouTube upload: one public Short per run folder, only ever to the channel's own YouTube channel. The Google
/// Cloud OAuth client is the Desktop app client shared with Flopômetro; each channel's sign-in (refresh token) is its own.
/// The variable and file names are empty here and filled from <see cref="ChannelOptions"/> by <see cref="Channel.Load"/>.
/// </summary>
public sealed record YouTubeOptions
{
    /// <summary>The only YouTube channel this channel uploads to. A sign-in or token for any other is refused.</summary>
    public string ChannelId { get; init; } = "";

    /// <summary>27 is Education.</summary>
    public string CategoryId { get; init; } = "27";
    public string Language { get; init; } = "pt-BR";
    public string AudioLanguage { get; init; } = "pt-BR";
    public string PrivacyStatus { get; init; } = "public";
    public bool MadeForKids { get; init; }

    /// <summary>
    /// YouTube's "altered or synthetic content" flag is for realistic depictions of people, places or events that did
    /// not happen. Archive photos and footage with an AI narrator reading facts are not that, so it is off.
    /// </summary>
    public bool ContainsSyntheticMedia { get; init; }

    /// <summary>Adds " #shorts" to the title when it still fits in 100 characters.</summary>
    public bool AppendShortsTag { get; init; } = true;

    /// <summary>
    /// The Desktop app OAuth client (Google Cloud JSON), &lt;tokenFolder&gt;/client_secret.json. On a runner,
    /// &lt;prefix&gt;_GOOGLE_CLIENT_ID and &lt;prefix&gt;_GOOGLE_CLIENT_SECRET replace it.
    /// </summary>
    public string ClientSecretFile { get; init; } = "";
    public string ClientIdEnvVar { get; init; } = "";
    public string ClientSecretEnvVar { get; init; } = "";

    /// <summary>
    /// The refresh token, DPAPI-encrypted for the Windows user (&lt;tokenFolder&gt;/youtube-token.bin). The environment
    /// variable &lt;prefix&gt;_YOUTUBE_REFRESH_TOKEN wins when set (Linux runner).
    /// </summary>
    public string TokenFile { get; init; } = "";
    public string RefreshTokenEnvVar { get; init; } = "";
}

/// <summary>
/// The TikTok upload: the video goes to the owner's TikTok inbox as a draft (Content Posting API, scope video.upload),
/// and the owner finishes the post in the app. The inbox flow has no caption field, so the caption is written to a file.
/// </summary>
public sealed record TikTokOptions
{
    /// <summary>&lt;prefix&gt;_TIKTOK_CLIENT_KEY and _CLIENT_SECRET, filled by <see cref="Channel.Load"/>.</summary>
    public string ClientKeyEnvVar { get; init; } = "";
    public string ClientSecretEnvVar { get; init; } = "";

    /// <summary>
    /// Must be registered exactly like this in the TikTok app's Login Kit settings (Desktop). TikTok allows only
    /// localhost or 127.0.0.1 with a port for desktop apps.
    /// </summary>
    public string RedirectUri { get; init; } = "http://localhost:8723/callback/";

    /// <summary>&lt;tokenFolder&gt;/tiktok-token.bin and &lt;prefix&gt;_TIKTOK_REFRESH_TOKEN, filled by <see cref="Channel.Load"/>.</summary>
    public string TokenFile { get; init; } = "";
    public string RefreshTokenEnvVar { get; init; } = "";

    /// <summary>TikTok's caption limit.</summary>
    public int MaxCaptionChars { get; init; } = 2200;

    /// <summary>
    /// Broad tags people browse in this niche, added after the video's own tags. TikTok has no public API for
    /// trending hashtags, so these are steady pt-BR history and learning tags instead of guesses.
    /// </summary>
    public IReadOnlyList<string> DiscoveryHashtags { get; init; } = ["historia", "curiosidades", "aprendanotiktok"];

    /// <summary>The video's own hashtags in a caption (the most specific ones the writer gave, first).</summary>
    public int MaxOwnHashtags { get; init; } = 4;

    /// <summary>Hashtags in a caption, the video's own then the discovery tags. A handful works better than a wall.</summary>
    public int MaxHashtags { get; init; } = 7;

    /// <summary>How long to wait for TikTok to finish processing the upload before leaving it to the app.</summary>
    public int StatusWaitSeconds { get; init; } = 300;
}
