using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinutoUtil.Core.Media;

/// <summary>
/// The footage of a channel with media.footage "trailers": the videos named with --youtube, else the game's footage
/// release (<see cref="FootageCache"/>, made on the owner's PC), else its Steam trailers, else (a game Steam does not
/// sell, like GTA VI) its official trailers on YouTube. The list is saved as trailers.json and reused by a re-render.
/// </summary>
public sealed class GameTrailers(SteamTrailers steam, YouTubeTrailers youTube, MediaOptions options, FootageCache? cache = null)
{
    public async Task<IReadOnlyList<MediaItem>> GatherAsync(string game, string mediaFolder, CancellationToken ct)
    {
        var manifestPath = Path.Combine(mediaFolder, "trailers.json");
        if (File.Exists(manifestPath))
        {
            // By file name: a run folder restored in another place (the cloud) keeps its trailers next to the manifest.
            var saved = (JsonSerializer.Deserialize<List<MediaItem>>(await File.ReadAllTextAsync(manifestPath, ct), Json.Options) ?? [])
                .Select(t => t with { LocalPath = Path.Combine(mediaFolder, "trailers", Path.GetFileName(t.LocalPath ?? "")) }).ToList();
            if (saved.Count > 0 && saved.All(t => File.Exists(t.LocalPath)))
            {
                Log.Info($"trailers: reused {saved.Count} from {manifestPath}");
                return saved;
            }
        }

        IReadOnlyList<MediaItem> trailers;
        if (options.YouTubeVideos.Count > 0)
        {
            trailers = await youTube.NamedAsync(options.YouTubeVideos, mediaFolder, ct);
        }
        else if (cache is not null && await cache.DownloadAsync(game, mediaFolder, ct) is { } cached)
        {
            trailers = cached;
        }
        else
        {
            try
            {
                trailers = await steam.GatherAsync(game, options.SteamAppId, mediaFolder, ct);
            }
            catch (MediaException steamError) when (options.SteamAppId is null)
            {
                Log.Warn($"trailers: {steamError.Message} Trying YouTube.");
                try
                {
                    trailers = await youTube.SearchAsync(game, mediaFolder, ct);
                }
                catch (MediaException youTubeError)
                {
                    throw new MediaException($"{steamError.Message} {youTubeError.Message} On the PC, `minuto-util footage --channel " +
                                             $"<id> --article \"{game}\"` downloads them for the cloud (release {FootageCache.ReleaseTag(game)}).");
                }
            }
        }
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(trailers, Json.Options), ct);
        return trailers;
    }
}

/// <summary>How a game's name is compared across Wikipedia, Steam and YouTube.</summary>
public static partial class GameNames
{
    /// <summary>The name without Wikipedia's disambiguation: "Hades (video game)" is "Hades".</summary>
    public static string Title(string game) => WithoutQualifier().Replace(game, "").Trim();

    /// <summary>Letters and digits only, folded: "The Witcher 3: Wild Hunt" and "the witcher 3 wild hunt" match.</summary>
    public static string Key(string text) => NotAlphanumeric().Replace(TextUtil.Fold(SteamTrailers.Clean(text)), "");

    /// <summary>Whether a text has the game's name as whole words: "Grand Theft Auto VI Trailer 2", not "Grand Theft Auto: Vice City".</summary>
    public static bool Names(string text, string game) =>
        $" {TextUtil.Fold(SteamTrailers.Clean(text))} ".Contains($" {TextUtil.Fold(SteamTrailers.Clean(game))} ", StringComparison.Ordinal);

    /// <summary>Wikipedia's disambiguation in a title: "Hades (video game)", "God of War (2018 video game)".</summary>
    [GeneratedRegex(@"\s*\((\d{4} )?(video ?game|jogo eletrônico|jogo|game)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WithoutQualifier();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NotAlphanumeric();
}
