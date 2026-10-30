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
    public const string PublishPrefix = "pub:", RejectPrefix = "rej:", RedoPrefix = "redo:", IdeaPrefix = "idea:",
        TopPrefix = "qt:", RemovePrefix = "qd:";

    private static readonly TimeSpan Brasilia = TimeSpan.FromHours(-3);

    /// <summary>
    /// The next <paramref name="count"/> scheduled render times after <paramref name="now"/>, from the channel's
    /// scheduleUtcHours (the cron lines of .github/workflows/render.yml; a test keeps them in step). None without a schedule.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> NextSlots(IReadOnlyList<int> scheduleUtcHours, DateTimeOffset now, int count)
    {
        var slots = new List<DateTimeOffset>();
        var hours = scheduleUtcHours.Distinct().Order().ToList();
        if (hours.Count == 0) return slots;
        for (var day = now.UtcDateTime.Date; slots.Count < count; day = day.AddDays(1))
            foreach (var hour in hours)
            {
                var slot = new DateTimeOffset(day.AddHours(hour), TimeSpan.Zero);
                if (slot > now && slots.Count < count) slots.Add(slot);
            }
        return slots;
    }

    /// <summary>The scheduled render hours in Brasília time, like "11h e 19h", for the bot's messages.</summary>
    public static string ScheduleHours(IReadOnlyList<int> scheduleUtcHours)
    {
        var hours = scheduleUtcHours.Select(h => (h + 24 + Brasilia.Hours) % 24).Distinct().Order().Select(h => $"{h:00}h").ToList();
        return hours.Count <= 1 ? string.Concat(hours) : $"{string.Join(", ", hours.SkipLast(1))} e {hours[^1]}";
    }

    /// <summary>"hoje às 15h", "amanhã às 07h", "sexta às 11h", in Brasília time.</summary>
    public static string When(DateTimeOffset slot, DateTimeOffset now)
    {
        var local = slot.ToOffset(Brasilia);
        var days = (local.Date - now.ToOffset(Brasilia).Date).Days;
        var day = days switch
        {
            0 => "hoje",
            1 => "amanhã",
            _ => local.ToString("dddd", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")).Replace("-feira", ""),
        };
        return $"{day} às {local:HH}h";
    }

    /// <summary>
    /// /status: the channel's name, the switches, what is being made or waiting for the owner, the last results and the
    /// next topics with the scheduled render that takes each.
    /// </summary>
    public static string StatusMessage(ChannelOptions channel, BotSettings settings, IReadOnlyList<RunRecord> runs, IReadOnlyList<QueueEntry> queue, DateTimeOffset now)
    {
        var text = new StringBuilder($"ℹ️ {channel.Name}\n");
        text.AppendLine(settings.Paused ? "⏸️ Vídeos agendados pausados (/retomar volta)."
            : channel.ScheduleUtcHours.Count == 0 ? "▶️ Sem vídeos agendados: só /novo."
            : $"▶️ Vídeos às {ScheduleHours(channel.ScheduleUtcHours)} (/pausar para).");
        text.AppendLine(settings.Review ? "📝 Revisão de roteiro ligada (/revisar off desliga)." : "📝 Revisão de roteiro desligada (/revisar on liga).");
        string Line(RunRecord r) => $"• {r.Title ?? r.Article} ({r.UpdatedAt.ToOffset(TimeSpan.FromHours(-3)):dd/MM HH:mm})";
        void Group(string heading, IEnumerable<RunRecord> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            text.AppendLine().AppendLine(heading);
            foreach (var r in list.Take(5)) text.AppendLine(Line(r) + (r.Note is { Length: > 0 } note && r.Status == RunStatus.Failed ? $": {TextUtil.Truncate(note, 80)}" : ""));
        }
        var recent = runs.Where(r => now - r.UpdatedAt < TimeSpan.FromDays(3)).OrderByDescending(r => r.UpdatedAt).ToList();
        Group("⚙️ Fazendo agora:", recent.Where(r => r.Status is RunStatus.Rendering or RunStatus.Publishing && now - r.UpdatedAt < TimeSpan.FromHours(1)));
        Group("📝 Roteiros esperando você:", recent.Where(r => r.Status == RunStatus.Script));
        Group("🎬 Prontos, esperando você tocar ✅ Publicar no vídeo (nada vai ao ar sem isso):", recent.Where(r => r.Status == RunStatus.Review));
        Group("✅ Publicados (3 dias):", recent.Where(r => r.Status == RunStatus.Published));
        Group("⚠️ Falharam (3 dias):", recent.Where(r => r.Status == RunStatus.Failed));
        // The next topics with the scheduled render that will take each one; the video reaches Telegram about 15
        // minutes later (the script first, in review mode) and goes out when the owner taps Publicar.
        text.AppendLine().AppendLine("📋 Próximos vídeos:");
        if (queue.Count == 0) text.AppendLine("Fila vazia: mande um tema.");
        var slots = NextSlots(channel.ScheduleUtcHours, now, 3);
        for (var i = 0; i < Math.Min(3, queue.Count); i++)
        {
            var title = (queue[i].Topic.PortugueseArticle ?? queue[i].Topic.Article) + (queue[i].Format is { } format ? $" ({format})" : "");
            text.AppendLine(settings.Paused ? $"{i + 1}. {title} (pausado)"
                : i < slots.Count ? $"{i + 1}. {title}: {When(slots[i], now)}"
                : $"{i + 1}. {title}");
        }
        if (!settings.Paused && queue.Count > 0)
            text.AppendLine(settings.Review
                ? "O roteiro chega na hora marcada; o vídeo, uns 15 min depois de você aprovar."
                : "Cada vídeo chega aqui uns 15 min depois da hora marcada e vai ao ar quando você toca ✅ Publicar.");
        return text.ToString().TrimEnd();
    }

    /// <summary>Items shown by /fila; the rest is counted.</summary>
    public const int QueueShown = 10;

    /// <summary>
    /// /fila: the next videos in order, where each came from, and per item a ⏫ (to the top) and a 🗑 (remove) button,
    /// two per row. The buttons carry the item's key, so they still point at the right item after the queue moves.
    /// </summary>
    public static (string Text, IReadOnlyList<TelegramButton> Buttons) QueueMessage(IReadOnlyList<QueueEntry> queue, string? done = null)
    {
        var text = new StringBuilder();
        if (done is not null) text.AppendLine(done).AppendLine();
        if (queue.Count == 0) return (text.Append("📭 A fila está vazia. Mande um tema para o próximo vídeo.").ToString(), []);
        text.AppendLine("📋 Próximos vídeos (o primeiro é o próximo):");
        for (var i = 0; i < Math.Min(queue.Count, QueueShown); i++)
        {
            var (_, topic, origin, format) = queue[i];
            var from = origin switch { QueueOrigin.Text => "sua ideia", QueueOrigin.Tapped => "da lista do dia", _ => "fila padrão" };
            text.Append($"\n{i + 1}. {topic.PortugueseArticle ?? topic.Article} ({TextUtil.YearLabel(topic.Year)}){(format is null ? "" : $" · {format}")} · {from}");
        }
        if (queue.Count > QueueShown) text.Append($"\n... e mais {queue.Count - QueueShown}");
        text.Append("\n\n⏫ leva para o topo, 🗑 tira da fila. Também: /mover 5 2, /remover 3, ou mande um tema para adicionar.");
        var buttons = queue.Take(QueueShown).SelectMany((e, i) => new[]
        {
            new TelegramButton($"⏫ {i + 1}", TopPrefix + e.Key), new TelegramButton($"🗑 {i + 1}", RemovePrefix + e.Key),
        }).ToList();
        return (text.ToString(), buttons);
    }

    /// <summary>
    /// The daily list: numbered ideas (Portuguese title when there is one, year, series, what inspired it) and a button
    /// per idea. A tap approves that idea for the next videos; a text message from the owner is an idea of its own.
    /// </summary>
    public static (string Text, IReadOnlyList<TelegramButton> Buttons) IdeaListMessage(IdeaList list)
    {
        var text = new StringBuilder("💡 Ideias para os próximos vídeos. Toque nos números que você quer; ou mande uma mensagem com o tema ou a pergunta que preferir.\n");
        for (var i = 0; i < list.Ideas.Count; i++)
        {
            var idea = list.Ideas[i];
            text.Append($"\n{i + 1}. {idea.PortugueseArticle ?? idea.Article} ({TextUtil.YearLabel(idea.Year)}) · {idea.Topic}");
            if (idea.Trend is { Length: > 0 } trend) text.Append($" · por: {trend}");
        }
        var buttons = list.Ideas.Select((_, i) => new TelegramButton($"{i + 1}", $"{IdeaPrefix}{list.Date}:{i + 1}")).ToList();
        return (TextUtil.Truncate(text.ToString(), 4000), buttons);
    }

    /// <summary>Publish, make the same topic again with a new script, voice and pictures, or drop it.</summary>
    public static IReadOnlyList<TelegramButton> ReviewButtons(string tag) =>
        [new("✅ Publicar", PublishPrefix + RunTag.Validate(tag)), new("🔁 Refazer", RedoPrefix + tag), new("❌ Rejeitar", RejectPrefix + tag)];

    /// <summary>
    /// The topic of an earlier run, for a remake: the old run is marked rejected (its video is dropped by the
    /// workflow) and the new run takes the same article, Portuguese article, year, series and inspiration.
    /// </summary>
    public BacklogTopic Remake(string oldTag)
    {
        var old = state.Find(oldTag) ?? throw new PublishRefusedException($"Run {oldTag} is not in the state branch.");
        if (old.Status is RunStatus.Published or RunStatus.Publishing)
            throw new PublishRefusedException($"Run {oldTag} is {old.Status.ToString().ToLowerInvariant()}; it is not remade.");
        state.Save(old with { Status = RunStatus.Rejected, Note = "remade", UpdatedAt = time.GetUtcNow() });
        return new BacklogTopic(old.Article, old.Year ?? 0, old.Topic, old.Trend, old.PortugueseArticle);
    }

    /// <summary>The caption under the cover image, sent right after the review video.</summary>
    public const string CoverCaption = "🖼️ Capa do vídeo. No YouTube ela entra sozinha ao publicar. No TikTok ela é o primeiro quadro do " +
                                       "vídeo: ao postar o rascunho, em Capa, deixe a seleção no começo.";

    /// <summary>The caption under the review video: what it is, how long, which voice, and the run's warnings to check.</summary>
    public static string ReviewCaption(RunRecord run, VideoMetadata metadata, IEnumerable<string> logLines)
    {
        var text = new StringBuilder();
        text.AppendLine($"🎬 {metadata.Title}");
        text.AppendLine($"{run.Topic} · {run.Article} ({TextUtil.YearLabel(run.Year)})");
        if (run.Trend is { Length: > 0 } trend) text.AppendLine($"💡 Inspiração: {trend}");
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
        var cover = record.YouTube?.Thumbnail is { } t && t.StartsWith("failed: ", StringComparison.Ordinal)
            ? $"\n⚠️ A capa não foi para o YouTube ({TextUtil.Truncate(t["failed: ".Length..], 200)}). Dá para pôr no YouTube Studio."
            : "";
        await send($"✅ Publicado: {run.Title}\nYouTube: {record.YouTube?.Url}\nTikTok: rascunho na caixa de entrada (Notificações do sistema).{cover}", [], ct);
        return 0;
    }

    /// <summary>How many of the latest TikTok drafts /tiktok looks up.</summary>
    public const int TikTokDraftsChecked = 5;

    /// <summary>
    /// Asks TikTok whose account the sign-in belongs to and where the latest drafts are, for when a draft never shows up
    /// in the app: the drafts go only to the signed-in account (in Sandbox, a target user of the app).
    /// </summary>
    public async Task<int> TikTokCheckAsync(Func<CancellationToken, Task<TikTokAccount>> account,
        Func<string, CancellationToken, Task<TikTokStatus>> status, CancellationToken ct)
    {
        var who = await account(ct);
        Log.Info($"TikTok: signed in as \"{who.DisplayName}\" (open_id {who.OpenId}, scopes {who.Scopes})");
        var drafts = new List<(RunRecord Run, TikTokStatus? Status, string? Error)>();
        foreach (var run in state.Runs().Where(r => r.TikTokPublishId is not null).OrderByDescending(r => r.UpdatedAt).Take(TikTokDraftsChecked))
        {
            try
            {
                var s = await status(run.TikTokPublishId!, ct);
                Log.Info($"TikTok: {run.Tag} {run.TikTokPublishId} is {s.Status}{(s.FailReason is null ? "" : $" ({s.FailReason})")}");
                drafts.Add((run, s, null));
            }
            catch (TikTokUploadException ex)
            {
                Log.Warn($"TikTok: {run.Tag} {run.TikTokPublishId}: {ex.Message}");
                drafts.Add((run, null, ex.Message));
            }
        }
        await send(TikTokCheckMessage(who, drafts), [], ct);
        return 0;
    }

    public static string TikTokCheckMessage(TikTokAccount account, IReadOnlyList<(RunRecord Run, TikTokStatus? Status, string? Error)> drafts)
    {
        var text = new StringBuilder("🎵 TikTok\n");
        text.AppendLine($"Conta conectada: {(account.DisplayName is { Length: > 0 } name ? name : "(sem nome)")}");
        text.AppendLine("Os rascunhos vão só para essa conta. Se não é a conta que você abre no app, faça o login de novo com a conta certa.");
        if (drafts.Count == 0) text.AppendLine("\nNenhum rascunho enviado ainda.");
        else text.AppendLine($"\nÚltimos rascunhos:");
        foreach (var (run, status, error) in drafts)
        {
            var what = (status?.Status, error) switch
            {
                (_, { } e) => $"erro ao consultar: {TextUtil.Truncate(e, 150)}",
                ("SEND_TO_USER_INBOX", _) => "na caixa de entrada, esperando você postar",
                ("PUBLISH_COMPLETE", _) => "postado",
                ("FAILED", _) => $"falhou: {status!.FailReason ?? "sem motivo"}",
                (var s, _) => $"ainda processando ({s})",
            };
            text.AppendLine($"• {run.Title ?? run.Article} ({run.UpdatedAt.ToOffset(Brasilia):dd/MM HH:mm}): {what}");
        }
        return text.ToString().TrimEnd();
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
