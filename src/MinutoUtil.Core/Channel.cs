using System.Text.RegularExpressions;

namespace MinutoUtil.Core;

/// <summary>
/// One channel the engine runs: its files live together in channels/&lt;id&gt;/ (config.json, topics.json,
/// prompts/writer.md, prompts/trends.md). <see cref="Config"/> is resolved: every secret's variable name comes from the
/// channel's env prefix, the sign-in files from its token folder and the watermark text from its name, unless config.json
/// names them itself. Nothing channel-specific falls back to another channel's values.
/// </summary>
public sealed partial record Channel(string Id, string Folder, AppConfig Config)
{
    /// <summary>The live channel, and the default of --channel, so every command line from before channels still works.</summary>
    public const string DefaultId = "minuto-util";

    public string TopicsFile => Path.Combine(Folder, "topics.json");
    public string WriterPromptFile => Path.Combine(Folder, "prompts", "writer.md");
    public string TrendsPromptFile => Path.Combine(Folder, "prompts", "trends.md");

    /// <summary>The topics file as the owner knows it, for Telegram messages ("channels/minuto-util/topics.json").</summary>
    public string TopicsFileName => $"channels/{Id}/topics.json";

    /// <summary>The channel's folder inside a state branch checkout: the root for Minuto Útil, channels/&lt;id&gt; otherwise.</summary>
    public string StateFolder(string stateCheckout) =>
        Path.GetFullPath(Path.Combine(stateCheckout, Config.Channel.StateFolder));

    /// <summary>Ids come from the command line and workflow inputs, so anything else is refused before it reaches a path.</summary>
    public static string ValidateId(string? id) =>
        id is not null && IdPattern().IsMatch(id) ? id : throw new ArgumentException($"\"{TextUtil.Truncate(id ?? "", 40)}\" is not a channel id (a-z, 0-9 and -).");

    /// <summary>Every channel folder that has a config.json, by id.</summary>
    public static IReadOnlyList<string> Ids(string repositoryRoot)
    {
        var folder = Path.Combine(repositoryRoot, "channels");
        return Directory.Exists(folder)
            ? Directory.GetDirectories(folder).Where(d => File.Exists(Path.Combine(d, "config.json"))).Select(Path.GetFileName).OfType<string>()
                .Where(id => IdPattern().IsMatch(id)).Order(StringComparer.Ordinal).ToList()
            : [];
    }

    public static Channel Load(string repositoryRoot, string? id = null)
    {
        id = ValidateId(id ?? DefaultId);
        var folder = Path.Combine(repositoryRoot, "channels", id);
        var path = Path.Combine(folder, "config.json");
        if (!File.Exists(path))
            throw new ArgumentException($"There is no channel \"{id}\" (no channels/{id}/config.json). Channels: {string.Join(", ", Ids(repositoryRoot))}.");
        return new Channel(id, folder, Resolve(id, AppConfig.Load(path)));
    }

    /// <summary>Fills the names derived from the channel and checks the ones each channel must set itself.</summary>
    internal static AppConfig Resolve(string id, AppConfig config)
    {
        var c = config.Channel;
        if (!string.IsNullOrEmpty(c.Id) && c.Id != id) throw new InvalidDataException($"channels/{id}/config.json says its id is \"{c.Id}\".");
        if (string.IsNullOrWhiteSpace(c.Name)) throw new InvalidDataException($"channels/{id}/config.json needs channel.name.");
        if (!PrefixPattern().IsMatch(c.EnvPrefix)) throw new InvalidDataException($"channels/{id}/config.json needs channel.envPrefix (upper case letters, digits and _).");
        if (string.IsNullOrWhiteSpace(c.TokenFolder)) throw new InvalidDataException($"channels/{id}/config.json needs channel.tokenFolder.");
        // Written out in every config (tools/channels.py reads the same fields for the workflows): "." only for Minuto
        // Útil, whose state was at the root before channels; every other channel channels/<id>, so none can share files.
        if (c.StateFolder != (id == DefaultId ? "." : $"channels/{id}"))
            throw new InvalidDataException($"channels/{id}/config.json: channel.stateFolder must be \"channels/{id}\".");
        if (c.ReleasePrefix != (id == DefaultId ? "run-" : $"run-{id}-"))
            throw new InvalidDataException($"channels/{id}/config.json: channel.releasePrefix must be \"run-{id}-\".");
        if (c.ScheduleUtcHours.Concat(c.IdeasUtcHours).Any(h => h is < 0 or > 23))
            throw new InvalidDataException($"channels/{id}/config.json: the schedule hours are UTC hours, 0 to 23.");

        var prefix = c.EnvPrefix;
        var tokens = c.TokenFolder.TrimEnd('/', '\\');
        return config with
        {
            Channel = c with { Id = id },
            YouTube = config.YouTube with
            {
                ClientSecretFile = Or(config.YouTube.ClientSecretFile, $"{tokens}/client_secret.json"),
                ClientIdEnvVar = Or(config.YouTube.ClientIdEnvVar, $"{prefix}_GOOGLE_CLIENT_ID"),
                ClientSecretEnvVar = Or(config.YouTube.ClientSecretEnvVar, $"{prefix}_GOOGLE_CLIENT_SECRET"),
                TokenFile = Or(config.YouTube.TokenFile, $"{tokens}/youtube-token.bin"),
                RefreshTokenEnvVar = Or(config.YouTube.RefreshTokenEnvVar, $"{prefix}_YOUTUBE_REFRESH_TOKEN"),
            },
            TikTok = config.TikTok with
            {
                ClientKeyEnvVar = Or(config.TikTok.ClientKeyEnvVar, $"{prefix}_TIKTOK_CLIENT_KEY"),
                ClientSecretEnvVar = Or(config.TikTok.ClientSecretEnvVar, $"{prefix}_TIKTOK_CLIENT_SECRET"),
                TokenFile = Or(config.TikTok.TokenFile, $"{tokens}/tiktok-token.bin"),
                RefreshTokenEnvVar = Or(config.TikTok.RefreshTokenEnvVar, $"{prefix}_TIKTOK_REFRESH_TOKEN"),
            },
            Telegram = config.Telegram with
            {
                BotTokenEnvVar = Or(config.Telegram.BotTokenEnvVar, $"{prefix}_TELEGRAM_BOT_TOKEN"),
                ChatIdEnvVar = Or(config.Telegram.ChatIdEnvVar, $"{prefix}_TELEGRAM_CHAT_ID"),
            },
            Video = config.Video with { Style = config.Video.Style with { WatermarkText = Or(config.Video.Style.WatermarkText, c.Name) } },
        };
    }

    private static string Or(string value, string derived) => string.IsNullOrWhiteSpace(value) ? derived : value;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,30}$")]
    private static partial Regex PrefixPattern();
}
