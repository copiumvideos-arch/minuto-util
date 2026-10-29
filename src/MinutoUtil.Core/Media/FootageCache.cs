using System.Text.Json;
using System.Text.RegularExpressions;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Core.Media;

/// <summary>
/// A game's trailers kept in a GitHub release (footage-&lt;game&gt;) of this repository, for footage the cloud cannot fetch
/// itself: YouTube asks GitHub's runners to sign in, while it serves the owner's PC. `minuto-util footage` downloads on
/// the PC and uploads here; a cloud run takes the release before trying Steam or YouTube. The gh CLI does the transfer
/// (GH_TOKEN in the cloud, the owner's login on the PC). The release is untrusted data like any download: only
/// manifest entries whose file is named like a trailer and is in the release are used.
/// </summary>
public sealed partial class FootageCache(string repositoryRoot)
{
    private const string Manifest = "trailers.json";

    /// <summary>The release of a game: "Grand Theft Auto VI" is footage-grand-theft-auto-vi.</summary>
    public static string ReleaseTag(string game)
    {
        var slug = string.Join('-', TextUtil.Fold(SteamTrailers.Clean(GameNames.Title(game))).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0) throw new MediaException("No game name for the footage release.");
        return "footage-" + (slug.Length > 60 ? slug[..60].TrimEnd('-') : slug);
    }

    /// <summary>The game's trailers from its release into the run's media/trailers/, or null when it has no release.</summary>
    public async Task<IReadOnlyList<MediaItem>?> DownloadAsync(string game, string mediaFolder, CancellationToken ct)
    {
        var tag = ReleaseTag(game);
        var folder = Path.Combine(mediaFolder, "trailers");
        Directory.CreateDirectory(folder);
        var result = await ProcessRunner.RunAsync("gh", ["release", "download", tag, "--dir", Path.GetFullPath(folder), "--clobber"],
            repositoryRoot, TimeSpan.FromMinutes(10), ct);
        if (result.ExitCode != 0)
        {
            if (result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase)) return null;
            throw new MediaException($"Could not download the footage release {tag}: {TextUtil.Truncate(result.StandardError.Trim(), 200)}");
        }
        var manifestPath = Path.Combine(folder, Manifest);
        var items = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<List<MediaItem>>(await File.ReadAllTextAsync(manifestPath, ct), Json.Options) ?? []
            : [];
        File.Delete(manifestPath);
        var trailers = items
            .Where(t => t.LocalPath is { } name && TrailerFile().IsMatch(name) && File.Exists(Path.Combine(folder, name)))
            .Select(t => t with { LocalPath = Path.Combine(folder, t.LocalPath!) })
            .ToList();
        if (trailers.Count == 0) throw new MediaException($"The footage release {tag} has no usable trailers.");
        Log.Info($"trailers: {trailers.Count} from the release {tag}, fetched on the owner's PC");
        return trailers;
    }

    /// <summary>Puts the trailers in the game's release, replacing what it had.</summary>
    public async Task<string> UploadAsync(string game, IReadOnlyList<MediaItem> trailers, string workFolder, CancellationToken ct)
    {
        var tag = ReleaseTag(game);
        // The manifest names each file only; the folder it lands in is the run's.
        var manifest = Path.Combine(workFolder, Manifest);
        await File.WriteAllTextAsync(manifest,
            JsonSerializer.Serialize(trailers.Select(t => t with { LocalPath = Path.GetFileName(t.LocalPath) }), Json.Options), ct);
        await ProcessRunner.RunAsync("gh", ["release", "delete", tag, "--cleanup-tag", "--yes"], repositoryRoot, TimeSpan.FromMinutes(2), ct);
        await ProcessRunner.RunCheckedAsync("gh",
            ["release", "create", tag, "--title", $"Footage: {GameNames.Title(game)}", "--latest=false",
             "--notes", "Official trailers fetched on the owner's PC (minuto-util footage), for cloud runs. Credits in trailers.json.",
             manifest, .. trailers.Select(t => Path.GetFullPath(t.LocalPath!))],
            repositoryRoot, TimeSpan.FromMinutes(30), ct);
        return tag;
    }

    [GeneratedRegex(@"^(steam|youtube)-[A-Za-z0-9_-]{1,40}\.mp4$")]
    private static partial Regex TrailerFile();
}
