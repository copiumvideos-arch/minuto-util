using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinutoUtil.Core.Publishing;

public sealed class PublishRefusedException(string message) : Exception(message);

/// <summary>published.json in a run folder: what went where, so the same video is never posted twice by accident.</summary>
public sealed record PublishRecord
{
    [JsonPropertyName("youtube")] public YouTubePublication? YouTube { get; init; }
    [JsonPropertyName("tiktok")] public TikTokPublication? TikTok { get; init; }
}

public sealed record YouTubePublication(string VideoId, string Url, string ChannelId, string PrivacyStatus, string Title, DateTimeOffset PublishedAt)
{
    /// <summary>"set" when the custom thumbnail went up, "failed: ..." when YouTube refused it, null when the run had none.</summary>
    public string? Thumbnail { get; init; }
}

public sealed record TikTokPublication(string PublishId, string Status, string CaptionFile, DateTimeOffset UploadedAt);

[Flags]
public enum Platforms
{
    None = 0,
    YouTube = 1,
    TikTok = 2,
    Both = YouTube | TikTok,
}

/// <summary>
/// Sends a finished run folder (video.mp4 + metadata.json) to YouTube as a public Short and to the owner's TikTok inbox
/// as a draft. The platform clients are made only when needed, so a dry run builds none and touches no network.
/// </summary>
/// <param name="sendText">Sends a message to the owner (Telegram), or null. TikTok's inbox upload has no caption field,
/// so the caption goes there after each draft, ready to copy.</param>
public sealed class Publisher(
    YouTubeOptions youtube,
    TikTokOptions tiktok,
    Func<YouTubeUploader> youtubeUploader,
    Func<TikTokUploader> tiktokUploader,
    TimeProvider time,
    Func<string, CancellationToken, Task>? sendText = null)
{
    public const string RecordFile = "published.json";
    public const string CaptionFile = "tiktok-caption.txt";

    /// <summary>Publishes to <paramref name="platforms"/>; a dry run returns the plan text and changes nothing.</summary>
    public async Task<string> PublishAsync(string folder, Platforms platforms, bool dryRun, bool force, CancellationToken ct)
    {
        var videoPath = Path.Combine(folder, "video.mp4");
        var metadataPath = Path.Combine(folder, "metadata.json");
        if (!File.Exists(videoPath)) throw new PublishRefusedException($"{videoPath} not found. Render the run first.");
        if (!File.Exists(metadataPath)) throw new PublishRefusedException($"{metadataPath} not found. Render the run first.");
        var metadata = JsonSerializer.Deserialize<VideoMetadata>(await File.ReadAllTextAsync(metadataPath, ct), Json.Options)
            ?? throw new InvalidDataException($"{metadataPath} is empty.");

        var record = LoadRecord(folder);
        var already = new List<string>();
        if (platforms.HasFlag(Platforms.YouTube) && record.YouTube is { } y) already.Add($"YouTube ({y.Url}, {y.PublishedAt:yyyy-MM-dd HH:mm})");
        if (platforms.HasFlag(Platforms.TikTok) && record.TikTok is { } t) already.Add($"TikTok (publish_id {t.PublishId}, {t.UploadedAt:yyyy-MM-dd HH:mm})");
        if (already.Count > 0 && !force && !dryRun)
            throw new PublishRefusedException($"This folder was already published to {string.Join(" and ", already)}. " +
                "Pick the other platform with --youtube or --tiktok, or pass --force to post it again.");

        var details = PublishCopy.YouTube(metadata, youtube);
        var caption = PublishCopy.TikTokCaption(metadata, tiktok);
        if (metadata.DurationSeconds > 180) Log.Warn($"the video is {metadata.DurationSeconds:0} s; YouTube treats only videos up to 3 minutes as Shorts");

        var thumbnailPath = Path.Combine(folder, Video.Thumbnail.FileName);
        var thumbnail = File.Exists(thumbnailPath) ? thumbnailPath : null;
        if (dryRun) return Plan(platforms, details, caption, new FileInfo(videoPath).Length, thumbnail, already);

        var summary = new StringBuilder();
        if (platforms.HasFlag(Platforms.YouTube))
        {
            var uploader = youtubeUploader();
            var channel = await uploader.EnsureChannelAsync(youtube.ChannelId, ct);
            Log.Info($"YouTube: uploading to channel {channel} ({youtube.ChannelId}), {details.PrivacyStatus}: {details.Title}");
            var video = await uploader.UploadAsync(videoPath, details, ct);
            record = record with { YouTube = new YouTubePublication(video.VideoId, video.Url, youtube.ChannelId, video.PrivacyStatus, details.Title, time.GetLocalNow()) };
            SaveRecord(folder, record);
            Log.Info($"YouTube: done, {video.Url} ({video.PrivacyStatus})");
            if (thumbnail is not null)
            {
                record = record with { YouTube = record.YouTube with { Thumbnail = await SetThumbnailAsync(uploader, video.VideoId, thumbnail, ct) } };
                SaveRecord(folder, record);
                summary.AppendLine($"YouTube thumbnail: {record.YouTube.Thumbnail}");
            }
            // A Google Cloud project that has not passed YouTube's API audit gets its uploads locked as private.
            if (video.PrivacyStatus != details.PrivacyStatus)
                Log.Warn($"YouTube answered privacy '{video.PrivacyStatus}', not '{details.PrivacyStatus}'; check the video in YouTube Studio");
            summary.AppendLine($"YouTube: {video.Url} ({video.PrivacyStatus})");
        }
        if (platforms.HasFlag(Platforms.TikTok))
        {
            // Written first: the owner needs it for the draft whatever happens to the status polling.
            await File.WriteAllTextAsync(Path.Combine(folder, CaptionFile), caption, new UTF8Encoding(false), ct);
            var upload = await tiktokUploader().UploadAsync(videoPath, TimeSpan.FromSeconds(tiktok.StatusWaitSeconds), ct);
            record = record with { TikTok = new TikTokPublication(upload.PublishId, upload.Status.Status, CaptionFile, time.GetLocalNow()) };
            SaveRecord(folder, record);
            Log.Info($"TikTok: draft {upload.PublishId} is {upload.Status.Status}");
            summary.AppendLine($"TikTok: draft sent ({upload.Status.Status}). Open the inbox notification in the TikTok app to finish the post; caption in {CaptionFile}" +
                (thumbnail is null ? "." : $", cover in {Video.Thumbnail.FileName}."));
            if (sendText is not null) summary.AppendLine(await SendCaptionAsync(caption, thumbnail is not null, ct));
        }
        return summary.ToString().TrimEnd();
    }

    /// <summary>
    /// Sets the run's thumbnail.jpg on its already published YouTube video (a new cover for an old video) and records the
    /// result in published.json. Refused when the run was never published or went to another channel.
    /// </summary>
    public async Task<string> ReplaceThumbnailAsync(string folder, CancellationToken ct)
    {
        var record = LoadRecord(folder);
        var published = record.YouTube ?? throw new PublishRefusedException($"{folder} has no YouTube video in {RecordFile}.");
        if (published.ChannelId != youtube.ChannelId)
            throw new PublishRefusedException($"The video {published.VideoId} is on channel {published.ChannelId}, not {youtube.ChannelId}; not touched.");
        var thumbnail = Path.Combine(folder, Video.Thumbnail.FileName);
        if (!File.Exists(thumbnail)) throw new PublishRefusedException($"{thumbnail} not found.");

        var result = await SetThumbnailAsync(youtubeUploader(), published.VideoId, thumbnail, ct);
        SaveRecord(folder, record with { YouTube = published with { Thumbnail = result } });
        return result;
    }

    /// <summary>
    /// The video is already public, so a refused thumbnail is only a warning: the reason goes into published.json and the
    /// owner can still set one in YouTube Studio.
    /// </summary>
    private static async Task<string> SetThumbnailAsync(YouTubeUploader uploader, string videoId, string path, CancellationToken ct)
    {
        try
        {
            await uploader.SetThumbnailAsync(videoId, path, ct);
            Log.Info("YouTube: thumbnail set");
            return "set";
        }
        catch (Exception ex) when ((ex is YouTubeUploadException or HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            Log.Warn($"YouTube: the thumbnail was not set: {ex.Message}");
            return $"failed: {TextUtil.Truncate(ex.Message, 300)}";
        }
    }

    /// <summary>
    /// Two messages: a short notice, then the caption alone so a long press copies exactly that. The draft is already
    /// in the inbox, so a Telegram failure is only a warning.
    /// </summary>
    private async Task<string> SendCaptionAsync(string caption, bool hasCover, CancellationToken ct)
    {
        try
        {
            await sendText!("Rascunho no TikTok pronto: abra a notificação no app. Legenda para copiar na próxima mensagem." +
                (hasCover ? " Para a capa, em Capa deixe a seleção no começo: o primeiro quadro é a capa." : ""), ct);
            await sendText(caption, ct);
            return "Telegram: caption sent.";
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            Log.Warn($"the TikTok caption could not be sent to Telegram: {ex.Message}");
            return $"Telegram: caption not sent ({ex.Message}); it is in {CaptionFile}.";
        }
    }

    private static string Plan(Platforms platforms, VideoDetails details, string caption, long videoBytes, string? thumbnail, List<string> already)
    {
        var text = new StringBuilder("DRY RUN: nothing is sent and no file is written.\n");
        foreach (var line in already) text.AppendLine($"Already published to {line}; a real run needs --force.");
        if (platforms.HasFlag(Platforms.YouTube))
        {
            text.AppendLine();
            text.AppendLine("== YouTube ==");
            text.AppendLine($"title ({details.Title.Length} chars): {details.Title}");
            text.AppendLine($"privacy: {details.PrivacyStatus}, category: {details.CategoryId}, language: {details.Language}, audio: {details.AudioLanguage}");
            text.AppendLine($"made for kids: {details.MadeForKids.ToString().ToLowerInvariant()}, synthetic media: {details.ContainsSyntheticMedia.ToString().ToLowerInvariant()}");
            text.AppendLine($"tags: {string.Join(", ", details.Tags)}");
            text.AppendLine(thumbnail is null ? "thumbnail: none (YouTube picks a frame)" : $"thumbnail: {Path.GetFileName(thumbnail)} ({new FileInfo(thumbnail).Length / 1024} KB)");
            text.AppendLine($"description ({Encoding.UTF8.GetByteCount(details.Description)} bytes):");
            text.AppendLine(details.Description);
        }
        if (platforms.HasFlag(Platforms.TikTok))
        {
            var plan = ChunkPlan.For(videoBytes);
            text.AppendLine();
            text.AppendLine("== TikTok (draft to the inbox) ==");
            text.AppendLine($"video: {videoBytes} bytes in {plan.TotalChunkCount} chunk(s) of {plan.ChunkSize} bytes");
            if (thumbnail is not null) text.AppendLine($"cover: {Path.GetFileName(thumbnail)}, picked by hand in the TikTok app (the inbox upload takes no cover)");
            text.AppendLine($"caption for {CaptionFile} ({caption.Length} chars):");
            text.AppendLine(caption);
        }
        return text.ToString().TrimEnd();
    }

    public static PublishRecord LoadRecord(string folder)
    {
        var path = Path.Combine(folder, RecordFile);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<PublishRecord>(File.ReadAllText(path), Json.Options) ?? new PublishRecord()
            : new PublishRecord();
    }

    private static void SaveRecord(string folder, PublishRecord record)
    {
        var path = Path.Combine(folder, RecordFile);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(record, Json.Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
