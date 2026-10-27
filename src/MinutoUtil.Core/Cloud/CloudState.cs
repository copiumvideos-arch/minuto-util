using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinutoUtil.Core.Cloud;

/// <summary>
/// One event to render: the English article, the year it happened and the series name, from config/topics.json or
/// from today's trends (<see cref="Trend"/> says which search inspired it).
/// </summary>
public sealed record BacklogTopic(string Article, int Year, string Topic, string? Trend = null);

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

/// <summary>The state branch checked out in a folder: runs/*.json plus the files the cloud runs keep between jobs.</summary>
public sealed class CloudState(string folder)
{
    /// <summary>A topic that failed this many times is skipped, so one broken article cannot block the queue.</summary>
    public const int MaxFailures = 2;

    public string Folder { get; } = Path.GetFullPath(folder);

    public string QuotaFile => Path.Combine(Folder, "quota-state.json");

    private string TrendFile => Path.Combine(Folder, "trending.json");

    public TrendQueue? Trending() =>
        File.Exists(TrendFile) ? JsonSerializer.Deserialize<TrendQueue>(File.ReadAllText(TrendFile), Json.Options) : null;

    public void SaveTrending(TrendQueue queue)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(TrendFile + ".tmp", JsonSerializer.Serialize(queue, Json.Options));
        File.Move(TrendFile + ".tmp", TrendFile, overwrite: true);
    }

    /// <summary>Every article rendered so far, whatever happened to it: the trend picker must not suggest them again.</summary>
    public IReadOnlyList<string> MadeArticles() => Runs().Select(r => r.Article).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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

    /// <summary>
    /// The next topic not rendered yet: today's trend picks first (while younger than <see cref="TrendQueue.Lifetime"/>),
    /// then the backlog in order. Any run that is not a failure uses its article up (a rejected video is not tried
    /// again), and an article that failed <see cref="MaxFailures"/> times is skipped. <c>Left</c> counts the backlog only.
    /// </summary>
    public (BacklogTopic? Next, int Left) NextTopic(TopicBacklog backlog, DateTimeOffset now)
    {
        var runs = Runs();
        bool Open(BacklogTopic t)
        {
            var mine = runs.Where(r => string.Equals(r.Article, t.Article, StringComparison.OrdinalIgnoreCase)).ToList();
            return mine.All(r => r.Status == RunStatus.Failed) && mine.Count < MaxFailures;
        }
        var left = backlog.Topics.Where(Open).ToList();
        var trend = Trending() is { } queue && now - queue.UpdatedAt < TrendQueue.Lifetime ? queue.Topics.FirstOrDefault(Open) : null;
        return (trend ?? left.FirstOrDefault(), left.Count);
    }
}
