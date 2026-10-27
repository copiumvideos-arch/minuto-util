using System.Text.Json;
using MinutoUtil.Core.Scripts;
using MinutoUtil.Core.Video;

namespace MinutoUtil.Core.Media;

/// <summary>The pictures and footage a run downloaded, saved as media/manifest.json so a re-render reuses them.</summary>
public sealed record MediaManifest(IReadOnlyList<MediaItem> Images, MediaItem? Clip)
{
    public IEnumerable<MediaItem> All => Clip is null ? Images : Images.Append(Clip);

    public async Task SaveAsync(string path, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, Json.Options), ct);

    public static MediaManifest? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var manifest = JsonSerializer.Deserialize<MediaManifest>(File.ReadAllText(path), Json.Options);
        // A manifest whose files were cleaned up is no cache at all.
        return manifest is not null && manifest.All.All(i => i.LocalPath is not null && File.Exists(i.LocalPath)) ? manifest : null;
    }
}

/// <summary>
/// Finds and downloads the pictures for each section from every image source (Commons, Library of Congress) with the
/// section's own search queries, and one real footage clip for the opening shot (Internet Archive's US National
/// Archives films first, then Commons videos). Results are ranked by <see cref="MediaScoring"/>, near duplicates
/// are dropped, and no file is used twice.
/// </summary>
public sealed class MediaGatherer(
    IReadOnlyList<IMediaSource> imageSources,
    IReadOnlyList<IMediaSource> videoSources,
    MediaDownloader downloader,
    ClipExtractor clips,
    MediaOptions options,
    int? eventYear)
{
    /// <summary>Hash distance at or below which two pictures count as the same photo.</summary>
    private const int DuplicateDistance = 10;

    /// <param name="stillsPerSection">How many still pictures each spoken section needs (index 0 is the hook).</param>
    /// <param name="clipSeconds">Length of the opening clip; 0 for none.</param>
    /// <param name="eventQueries">Searches for the event as a whole (the English article title), tried first for footage.</param>
    public async Task<MediaManifest> GatherAsync(VideoScript script, IReadOnlyList<int> stillsPerSection, double clipSeconds,
        IReadOnlyList<string> eventQueries, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var accepted = new List<MediaItem>();
        var seen = new HashSet<string>();
        for (var section = 0; section < stillsPerSection.Count; section++)
        {
            // The hook has no queries of its own: it shows the first section's subject.
            var source = script.Sections[Math.Clamp(section - 1, 0, script.Sections.Count - 1)];
            // One spare per section, so a picture that turns out unusable does not leave a gap.
            var wanted = stillsPerSection[section] + 1;
            var candidates = await CandidatesAsync([.. source.SearchEn, .. source.SearchPt], section, wanted * 4, seen, ct);
            var got = 0;
            foreach (var candidate in candidates)
            {
                if (got >= wanted) break;
                if (!seen.Add(candidate.Id)) continue;
                if (accepted.Any(a => MediaScoring.TitleKey(a.Title) == MediaScoring.TitleKey(candidate.Title))) continue;
                // A photo taken decades later (today's beach, a museum) is never the archive picture the story needs.
                if (candidate.Year >= eventYear + 30) continue;
                var side = Math.Min(candidate.Width, candidate.Height);
                if (side > 0 && side < options.MinImageShortSide) continue;
                var item = await downloader.DownloadImageAsync(candidate, folder, $"s{section}-{got + 1:00}-{TextUtil.Slug(candidate.Title, 40)}", ct);
                if (item is null) continue;
                if (accepted.FirstOrDefault(a => Ffmpeg.HashDistance(a.Hash!.Value, item.Hash!.Value) <= DuplicateDistance) is { } twin)
                {
                    Log.Info($"skipped {item.Id}: same photo as {twin.Id}");
                    File.Delete(item.LocalPath!);
                    continue;
                }
                accepted.Add(item with { Section = section });
                got++;
            }
            Log.Info($"media: section {section} has {got} picture(s) for {stillsPerSection[section]} shot(s)");
        }

        MediaItem? clip = null;
        if (clipSeconds > 0)
        {
            var queries = eventQueries.Concat(script.Sections.Take(2).SelectMany(s => s.SearchEn)).Distinct().ToList();
            clip = await FindClipAsync(queries, clipSeconds, folder, ct);
        }
        return new MediaManifest(accepted, clip);
    }

    /// <summary>
    /// Ranked candidates from the section's queries, English first. Stops asking once there are <paramref name="enough"/>
    /// new ones: fewer requests, and the later queries are the less precise ones anyway.
    /// </summary>
    private async Task<IReadOnlyList<MediaItem>> CandidatesAsync(IReadOnlyList<string> queries, int section, int enough,
        IReadOnlySet<string> seen, CancellationToken ct)
    {
        var best = new Dictionary<string, MediaItem>();
        foreach (var query in queries)
            foreach (var source in imageSources)
            {
                if (best.Keys.Count(k => !seen.Contains(k)) >= enough) break;
                var results = await source.SearchAsync(query, MediaKind.Image, options.ResultsPerQuery, ct);
                for (var rank = 0; rank < results.Count; rank++)
                {
                    var item = results[rank] with { Section = section };
                    item = item with { Score = MediaScoring.Score(item, rank, options.PenalizedWords, eventYear) };
                    if (!best.TryGetValue(item.Id, out var known) || item.Score > known.Score) best[item.Id] = item;
                }
            }
        return best.Values.OrderByDescending(i => i.Score).ToList();
    }

    private async Task<MediaItem?> FindClipAsync(IReadOnlyList<string> queries, double seconds, string folder, CancellationToken ct)
    {
        var tried = 0;
        foreach (var source in videoSources)
            foreach (var query in queries)
            {
                var results = await source.SearchAsync(query, MediaKind.Video, 8, ct);
                foreach (var candidate in results.Where(r => r.DurationSeconds >= seconds + 8))
                {
                    if (tried++ >= 4) return null;
                    Log.Info($"clip: trying {candidate.Id} ({candidate.Source}, {candidate.DurationSeconds:0}s, {candidate.Width}x{candidate.Height})");
                    var clip = await clips.ExtractAsync(candidate, folder, seconds, ct);
                    if (clip is not null)
                    {
                        Log.Info($"clip: {clip.Id} from {clip.ClipStart:0.0}s");
                        return clip;
                    }
                }
            }
        Log.Warn("clip: no usable footage found; the video opens on a still");
        return null;
    }
}
