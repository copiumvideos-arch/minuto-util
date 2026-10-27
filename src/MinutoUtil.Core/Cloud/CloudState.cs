using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinutoUtil.Core.Cloud;

/// <summary>
/// One event to render: the English article, the year it happened and the series name, from config/topics.json or
/// from today's trends or the owner's idea (<see cref="Trend"/> says what inspired it). <see cref="PortugueseArticle"/>
/// is the second source when it is not the English page's language link.
/// </summary>
public sealed record BacklogTopic(string Article, int Year, string Topic, string? Trend = null, string? PortugueseArticle = null);

public sealed record TopicBacklog(IReadOnlyList<BacklogTopic> Topics)
{
    public static TopicBacklog Load(string path) =>
        JsonSerializer.Deserialize<TopicBacklog>(File.ReadAllText(path), Json.Options) ?? throw new InvalidDataException($"{path} is empty.");
}

public enum RunStatus
{
    Rendering,
    Review,
    Publishing,
    Published,
    Rejected,
    Failed,
}

/// <summary>
/// What happened to one cloud run, kept as runs/&lt;tag&gt;.json on the repository's state branch. One file per run, so
/// the render and publish workflows never edit the same file and their commits never conflict.
/// </summary>
public sealed record RunRecord
{
    public required string Tag { get; init; }
    public required string Article { get; init; }
    public int? Year { get; init; }
    public string Topic { get; init; } = "";
    public string? Trend { get; init; }
    public string? PortugueseArticle { get; init; }
    public required RunStatus Status { get; init; }
    public string? Title { get; init; }
    public string? YouTubeUrl { get; init; }
    public string? TikTokPublishId { get; init; }
    public string? Note { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A run's name: its UTC start time, like 20261027-1114. Used in release names, Telegram buttons and file names.</summary>
public static partial class RunTag
{
    public static string For(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyyMMdd-HHmm");

    /// <summary>Tags arrive from Telegram buttons and workflow inputs, so anything else is refused before it reaches a path or a command.</summary>
    public static string Validate(string? tag) =>
        tag is not null && Pattern().IsMatch(tag) ? tag : throw new ArgumentException($"\"{TextUtil.Truncate(tag ?? "", 40)}\" is not a run tag (yyyyMMdd-HHmm).");

    [GeneratedRegex(@"^\d{8}-\d{4}$")]
    private static partial Regex Pattern();
}

public enum QueueOrigin
{
    Text,
    Tapped,
    Backlog,
}

/// <summary>One upcoming video: <see cref="Key"/> stays the same while the queue changes around it (for buttons).</summary>
public sealed record QueueEntry(string Key, BacklogTopic Topic, QueueOrigin Origin);

/// <summary>The state branch checked out in a folder: runs/*.json plus the files the cloud runs keep between jobs.</summary>
public sealed class CloudState(string folder)
{
    /// <summary>A topic that failed this many times is skipped, so one broken article cannot block the queue.</summary>
    public const int MaxFailures = 2;

    public string Folder { get; } = Path.GetFullPath(folder);

    public string QuotaFile => Path.Combine(Folder, "quota-state.json");

    private string IdeasFolder => Path.Combine(Folder, "ideas");

    private string ApprovedFolder => Path.Combine(Folder, "approved");

    public IdeaList? Ideas(string date)
    {
        var path = Path.Combine(IdeasFolder, $"{IdeaDate(date)}.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<IdeaList>(File.ReadAllText(path), Json.Options) : null;
    }

    public void SaveIdeas(IdeaList list) => Write(Path.Combine(IdeasFolder, $"{IdeaDate(list.Date)}.json"), list);

    public IReadOnlyList<ApprovedIdea> Approved() =>
        Directory.Exists(ApprovedFolder)
            ? Directory.GetFiles(ApprovedFolder, "*.json").Select(f => JsonSerializer.Deserialize<ApprovedIdea>(File.ReadAllText(f), Json.Options)!).ToList()
            : [];

    public void SaveApproved(ApprovedIdea idea)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(idea.Id, @"^[0-9a-z-]{1,40}$")) throw new ArgumentException($"\"{idea.Id}\" is not an idea id.");
        Write(Path.Combine(ApprovedFolder, $"{idea.Id}.json"), idea);
    }

    private static string IdeaDate(string date) =>
        System.Text.RegularExpressions.Regex.IsMatch(date, @"^\d{8}$") ? date : throw new ArgumentException($"\"{TextUtil.Truncate(date, 20)}\" is not a date (yyyyMMdd).");

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json.Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Every article rendered so far, whatever happened to it.</summary>
    public IReadOnlyList<string> MadeArticles() => Runs().Select(r => r.Article).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// What the picker must not suggest again: articles rendered, approved and waiting, or offered in the last three
    /// daily lists (a fresh list each day rather than the same ideas the owner passed on).
    /// </summary>
    public IReadOnlyList<string> TakenArticles()
    {
        var offered = Directory.Exists(IdeasFolder)
            ? Directory.GetFiles(IdeasFolder, "*.json").Order(StringComparer.Ordinal).TakeLast(3)
                .SelectMany(f => JsonSerializer.Deserialize<IdeaList>(File.ReadAllText(f), Json.Options)!.Ideas.Select(i => i.Article))
            : [];
        return MadeArticles().Concat(Approved().Select(a => a.Topic.Article)).Concat(offered).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string RunsFolder => Path.Combine(Folder, "runs");

    public IReadOnlyList<RunRecord> Runs() =>
        Directory.Exists(RunsFolder)
            ? Directory.GetFiles(RunsFolder, "*.json").Order(StringComparer.Ordinal)
                .Select(f => JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(f), Json.Options)!).ToList()
            : [];

    public RunRecord? Find(string tag)
    {
        var path = Path.Combine(RunsFolder, $"{RunTag.Validate(tag)}.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(path), Json.Options) : null;
    }

    public void Save(RunRecord record)
    {
        Directory.CreateDirectory(RunsFolder);
        var path = Path.Combine(RunsFolder, $"{RunTag.Validate(record.Tag)}.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(record, Json.Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private string OrderFile => Path.Combine(Folder, "order.json");

    private string RemovedFolder => Path.Combine(Folder, "removed");

    /// <summary>
    /// What the next videos will be, in order. By default: the owner's text ideas, then the ideas tapped in the daily
    /// lists (oldest first), then the backlog. The owner's own ordering (order.json, from /mover and the ⏫ button) comes
    /// before that default. Any run that is not a failure uses its article up (a rejected video is not tried again), an
    /// article that failed <see cref="MaxFailures"/> times is skipped, and so is one the owner removed.
    /// </summary>
    public IReadOnlyList<QueueEntry> Queue(TopicBacklog backlog)
    {
        var runs = Runs();
        var removed = Removed();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        bool Open(BacklogTopic t)
        {
            if (removed.Contains(TextUtil.Slug(t.Article, 60))) return false;
            var mine = runs.Where(r => string.Equals(r.Article, t.Article, StringComparison.OrdinalIgnoreCase)).ToList();
            return mine.All(r => r.Status == RunStatus.Failed) && mine.Count < MaxFailures;
        }
        var entries = Approved().OrderByDescending(a => a.FromText).ThenBy(a => a.ApprovedAt)
            .Select(a => new QueueEntry("a-" + a.Id, a.Topic, a.FromText ? QueueOrigin.Text : QueueOrigin.Tapped))
            .Concat(backlog.Topics.Select(t => new QueueEntry("b-" + TextUtil.Slug(t.Article, 34), t, QueueOrigin.Backlog)))
            .Where(e => Open(e.Topic))
            // The same article twice (tapped and in the backlog): the first one stands.
            .Where(e => keys.Add("article:" + e.Topic.Article.ToLowerInvariant()) && keys.Add(e.Key))
            .ToList();
        var order = File.Exists(OrderFile) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(OrderFile), Json.Options) ?? [] : [];
        var ordered = order.Select(k => entries.FirstOrDefault(e => e.Key == k)).OfType<QueueEntry>().ToList();
        return [.. ordered, .. entries.Except(ordered)];
    }

    /// <summary>The next topic, and how many backlog topics are left (the Telegram warning when it runs low).</summary>
    public (BacklogTopic? Next, int Left) NextTopic(TopicBacklog backlog)
    {
        var queue = Queue(backlog);
        return (queue.FirstOrDefault()?.Topic, queue.Count(e => e.Origin == QueueOrigin.Backlog));
    }

    /// <summary>The entry a key (from a button) or a 1-based position (from /mover, /remover) points at.</summary>
    public QueueEntry Find(TopicBacklog backlog, string item)
    {
        var queue = Queue(backlog);
        var entry = int.TryParse(item, out var position)
            ? position >= 1 && position <= queue.Count ? queue[position - 1] : null
            : queue.FirstOrDefault(e => e.Key == item);
        return entry ?? throw new ArgumentException($"The queue has no item {TextUtil.Truncate(item, 40)}.");
    }

    /// <summary>Puts an entry at a 1-based position; the order of everything above the end of the change is saved.</summary>
    public QueueEntry Move(TopicBacklog backlog, string item, int position)
    {
        var entry = Find(backlog, item);
        var keys = Queue(backlog).Select(e => e.Key).Where(k => k != entry.Key).ToList();
        keys.Insert(Math.Clamp(position, 1, keys.Count + 1) - 1, entry.Key);
        // Only the head is pinned (down to the new position, or as far as an earlier move pinned): the default order
        // still decides where new ideas and the rest of the backlog go.
        var pinnedBefore = File.Exists(OrderFile) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(OrderFile), Json.Options)?.Count ?? 0 : 0;
        // One more than before: the moved item joins the pinned head without pushing an earlier pin out of it.
        Write(OrderFile, keys.Take(Math.Max(position, pinnedBefore + 1)).ToList());
        return entry;
    }

    /// <summary>Takes an entry out of the queue for good: a removed idea's file goes, and its article is never picked again.</summary>
    public QueueEntry Remove(TopicBacklog backlog, string item)
    {
        var entry = Find(backlog, item);
        Write(Path.Combine(RemovedFolder, $"{TextUtil.Slug(entry.Topic.Article, 60)}.json"),
            new { article = entry.Topic.Article, removedAt = DateTimeOffset.UtcNow });
        if (entry.Origin != QueueOrigin.Backlog)
        {
            var path = Path.Combine(ApprovedFolder, $"{entry.Key[2..]}.json");
            if (File.Exists(path)) File.Delete(path);
        }
        return entry;
    }

    private HashSet<string> Removed() =>
        Directory.Exists(RemovedFolder)
            ? Directory.GetFiles(RemovedFolder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];
}
