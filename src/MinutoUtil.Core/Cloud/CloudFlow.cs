using System.Text;
using System.Text.Json;
using MinutoUtil.Core.Publishing;

namespace MinutoUtil.Core.Cloud;

/// <summary>Sends a Telegram message, with buttons when the list is not empty.</summary>
public delegate Task SendMessage(string text, IReadOnlyList<TelegramButton> buttons, CancellationToken ct);

/// <summary>
/// The steps of the cloud runs between the workflows and Telegram: the review message after a render, then publish or
/// reject when the owner taps a button. Every step writes the run's state record; the workflow commits it.
/// </summary>
public sealed class CloudFlow(CloudState state, SendMessage send, TimeProvider time)
{
    public const string PublishPrefix = "pub:", RejectPrefix = "rej:";

    public static IReadOnlyList<TelegramButton> ReviewButtons(string tag) =>
        [new("✅ Publicar", PublishPrefix + RunTag.Validate(tag)), new("❌ Rejeitar", RejectPrefix + tag)];

    /// <summary>The caption under the review video: what it is, how long, which voice, and the run's warnings to check.</summary>
    public static string ReviewCaption(RunRecord run, VideoMetadata metadata, IEnumerable<string> logLines)
    {
        var text = new StringBuilder();
        text.AppendLine($"🎬 {metadata.Title}");
        text.AppendLine($"{run.Topic} · {run.Article} ({run.Year})");
        text.AppendLine($"{metadata.DurationSeconds:0} s · voz {metadata.Voice} · roteiro {metadata.Writer}");
        // The log's warnings are what a reviewer should look or listen for (a section Whisper heard poorly, a reused picture).
        var warnings = logLines.Select(l => l.IndexOf(" WARN ", StringComparison.Ordinal) is var i and >= 0 ? l[(i + 6)..] : null)
            .OfType<string>().Where(w => !w.StartsWith("gemini:", StringComparison.Ordinal) && !w.StartsWith("openrouter:", StringComparison.Ordinal))
            .Distinct().ToList();
        if (warnings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Avisos:");
            foreach (var w in warnings.Take(6)) text.AppendLine($"• {TextUtil.Truncate(w, 140)}");
            if (warnings.Count > 6) text.AppendLine($"• mais {warnings.Count - 6} no run.log");
        }
        text.AppendLine();
        text.Append($"Publicar manda para o YouTube e um rascunho para o TikTok. Execução {run.Tag}.");
        return TextUtil.Truncate(text.ToString(), TelegramNotifier.MaxCaption);
    }

    /// <summary>
    /// Publishes an approved run to the platforms it has not reached yet (published.json in the folder, restored from
    /// the release), so tapping "Publicar" twice or retrying after a failure never posts a platform twice.
    /// </summary>
    public async Task<int> PublishAsync(string tag, string folder, Publisher publisher, CancellationToken ct)
    {
        var run = state.Find(tag) ?? throw new PublishRefusedException($"Run {tag} is not in the state branch.");
        if (run.Status == RunStatus.Published)
        {
            await send($"Já publicado: {run.Title} {run.YouTubeUrl}", [], ct);
            return 0;
        }
        if (run.Status == RunStatus.Rejected) throw new PublishRefusedException($"Run {tag} was rejected; it is not published.");

        var done = Publisher.LoadRecord(folder);
        var platforms = (done.YouTube is null ? Platforms.YouTube : Platforms.None) | (done.TikTok is null ? Platforms.TikTok : Platforms.None);
        state.Save(run with { Status = RunStatus.Publishing, UpdatedAt = time.GetUtcNow() });
        try
        {
            if (platforms != Platforms.None) Log.Info(await publisher.PublishAsync(folder, platforms, dryRun: false, force: false, ct));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // Whatever reached a platform is in published.json; a retry sends only the rest.
            var partial = Publisher.LoadRecord(folder);
            state.Save(run with { Status = RunStatus.Review, YouTubeUrl = partial.YouTube?.Url, TikTokPublishId = partial.TikTok?.PublishId,
                Note = $"publish failed: {TextUtil.Truncate(ex.Message, 300)}", UpdatedAt = time.GetUtcNow() });
            await send($"⚠️ Falhou ao publicar \"{run.Title}\": {TextUtil.Truncate(ex.Message, 300)}" +
                (partial.YouTube is { } y ? $"\nO YouTube já foi: {y.Url}" : ""), [new("🔁 Tentar de novo", PublishPrefix + tag)], ct);
            Log.Warn($"publish {tag} failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        var record = Publisher.LoadRecord(folder);
        state.Save(run with { Status = RunStatus.Published, YouTubeUrl = record.YouTube?.Url, TikTokPublishId = record.TikTok?.PublishId,
            Note = null, UpdatedAt = time.GetUtcNow() });
        await send($"✅ Publicado: {run.Title}\nYouTube: {record.YouTube?.Url}\nTikTok: rascunho na caixa de entrada (Notificações do sistema).", [], ct);
        return 0;
    }

    public async Task RejectAsync(string tag, CancellationToken ct)
    {
        var run = state.Find(tag) ?? throw new PublishRefusedException($"Run {tag} is not in the state branch.");
        if (run.Status is RunStatus.Published or RunStatus.Publishing)
            throw new PublishRefusedException($"Run {tag} is {run.Status.ToString().ToLowerInvariant()}; it cannot be rejected now.");
        state.Save(run with { Status = RunStatus.Rejected, UpdatedAt = time.GetUtcNow() });
        await send($"❌ Rejeitado: {run.Title}. O tema não volta para a fila.", [], ct);
    }

    /// <summary>A render that broke: the topic gets one more chance later, and the owner gets the link to the log.</summary>
    public async Task FailedAsync(string tag, string reason, string? logUrl, CancellationToken ct)
    {
        var run = state.Find(tag) ?? throw new PublishRefusedException($"Run {tag} is not in the state branch.");
        state.Save(run with { Status = RunStatus.Failed, Note = TextUtil.Truncate(reason, 300), UpdatedAt = time.GetUtcNow() });
        var failures = state.Runs().Count(r => r.Status == RunStatus.Failed && string.Equals(r.Article, run.Article, StringComparison.OrdinalIgnoreCase));
        var next = failures >= CloudState.MaxFailures ? "O tema saiu da fila." : "O tema volta na próxima execução.";
        await send($"⚠️ O vídeo sobre {run.Article} falhou: {TextUtil.Truncate(reason, 300)}\n{next}{(logUrl is null ? "" : $"\n{logUrl}")}", [], ct);
    }

    public static VideoMetadata ReadMetadata(string folder) =>
        JsonSerializer.Deserialize<VideoMetadata>(File.ReadAllText(Path.Combine(folder, "metadata.json")), Json.Options)
        ?? throw new InvalidDataException($"{folder}/metadata.json is empty.");
}
