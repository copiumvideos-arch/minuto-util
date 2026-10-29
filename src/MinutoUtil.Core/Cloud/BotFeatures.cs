using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinutoUtil.Core.Publishing;
using MinutoUtil.Core.Scripts;

namespace MinutoUtil.Core.Cloud;

/// <summary>The owner's switches, kept as settings.json on the state branch.</summary>
public sealed record BotSettings
{
    /// <summary>On: each run stops after the script and waits for the owner's approval or edit on Telegram.</summary>
    public bool Review { get; init; }

    /// <summary>On: the scheduled renders skip; /novo still makes a video.</summary>
    public bool Paused { get; init; }
}

/// <summary>
/// The script as the owner sees it on Telegram, and the owner's edit applied back. The message is plain labeled
/// lines ("Título:", "Gancho:", "1.", "Pergunta:", "Capa:", "Cena:"); a reply may carry all of them or only the ones that changed.
/// </summary>
public static partial class ScriptReview
{
    public const string Marker = "📝 Roteiro para revisar";

    public static string Message(RunRecord run, VideoScript script)
    {
        var text = new StringBuilder($"{Marker} · {run.Topic} · {run.Article} ({TextUtil.YearLabel(run.Year)})\n\n");
        text.AppendLine($"Título: {script.Title}");
        text.AppendLine($"Gancho: {script.Hook}");
        for (var i = 0; i < script.Sections.Count; i++) text.AppendLine($"{i + 1}. {script.Sections[i].Text}");
        if (!string.IsNullOrWhiteSpace(script.Question)) text.AppendLine($"Pergunta: {script.Question}");
        if (!string.IsNullOrWhiteSpace(script.ThumbnailText)) text.AppendLine($"Capa: {script.ThumbnailText}");
        if (!string.IsNullOrWhiteSpace(script.ThumbnailScene)) text.AppendLine($"Cena: {script.ThumbnailScene}");
        text.AppendLine();
        text.AppendLine("✏️ Para mudar, responda a esta mensagem com o texto corrigido: todas as linhas ou só as que mudaram, com o " +
                        "mesmo começo (Título:, Gancho:, 2., Pergunta:, Capa:, Cena:). Números por extenso soam melhor na voz. Na capa, a " +
                        "palavra entre *asteriscos* fica em vermelho. A cena é a pintura feita por IA atrás da capa, em inglês, sem " +
                        "nomes de pessoas.");
        text.Append($"Execução {run.Tag}");
        return TextUtil.Truncate(text.ToString(), 4000);
    }

    public static IReadOnlyList<TelegramButton> Buttons(string tag) =>
    [
        new("✅ Aprovar", "sok:" + RunTag.Validate(tag)), new("🔁 Outro roteiro", "snew:" + tag), new("❌ Rejeitar", CloudFlow.RejectPrefix + tag),
    ];

    /// <summary>
    /// The owner's reply applied to the script: each labeled line replaces that field; lines without a known label
    /// are ignored. Searches for pictures and the sources stay; the caller polishes the result like a written script.
    /// </summary>
    public static (VideoScript Script, int Changed) Apply(VideoScript script, string reply)
    {
        var sections = script.Sections.ToList();
        var (title, hook, question, cover, scene, changed) = (script.Title, script.Hook, script.Question, script.ThumbnailText, script.ThumbnailScene, 0);
        foreach (var raw in reply.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (Label(line, "Título") is { } t) (title, changed) = (t, changed + 1);
            else if (Label(line, "Gancho") is { } h) (hook, changed) = (h, changed + 1);
            else if (Label(line, "Pergunta") is { } q) (question, changed) = (q, changed + 1);
            else if (Label(line, "Capa") is { } c) (cover, changed) = (c, changed + 1);
            else if (Label(line, "Cena") is { } sc) (scene, changed) = (sc, changed + 1);
            else if (Numbered().Match(line) is { Success: true } m && int.Parse(m.Groups[1].Value) is var n && n >= 1 && n <= sections.Count
                     && m.Groups[2].Value.Trim() is { Length: > 0 } body)
            {
                sections[n - 1] = sections[n - 1] with { Text = body };
                changed++;
            }
        }
        return (script with { Title = TextUtil.Truncate(title, 100), Hook = hook, Sections = sections, Question = question, ThumbnailText = cover,
            ThumbnailScene = scene }, changed);
    }

    private static string? Label(string line, string label) =>
        line.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase) && line[(label.Length + 1)..].Trim() is { Length: > 0 } value ? value : null;

    [GeneratedRegex(@"^(\d{1,2})[.)]\s*(.+)$")]
    private static partial Regex Numbered();
}

/// <summary>Views, likes and comments of the published Shorts, from the YouTube Data API (videos.list, 1 quota unit).</summary>
public sealed record VideoStats(string VideoId, string Title, long Views, long Likes, long Comments, DateTimeOffset PublishedAt);

public static class YouTubeStats
{
    public static async Task<IReadOnlyList<VideoStats>> FetchAsync(HttpClient http, string accessToken, IReadOnlyList<string> videoIds, CancellationToken ct)
    {
        if (videoIds.Count == 0) return [];
        var stats = new List<VideoStats>();
        foreach (var chunk in videoIds.Chunk(50))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.googleapis.com/youtube/v3/videos?part=snippet,statistics&id={string.Join(',', chunk.Select(Uri.EscapeDataString))}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"YouTube videos.list returned {(int)response.StatusCode}: {TextUtil.Truncate(body, 200)}");
            using var doc = JsonDocument.Parse(body);
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var s = item.GetProperty("statistics");
                long Count(string name) => s.TryGetProperty(name, out var v) && long.TryParse(v.GetString(), out var n) ? n : 0;
                var snippet = item.GetProperty("snippet");
                stats.Add(new VideoStats(item.GetProperty("id").GetString()!, snippet.GetProperty("title").GetString() ?? "",
                    Count("viewCount"), Count("likeCount"), Count("commentCount"), snippet.GetProperty("publishedAt").GetDateTimeOffset()));
            }
        }
        return stats;
    }

    /// <summary>The Shorts' YouTube ids from the published runs (their "https://youtube.com/shorts/&lt;id&gt;" links).</summary>
    public static IReadOnlyList<string> Ids(IEnumerable<RunRecord> runs) =>
        runs.Where(r => r.Status == RunStatus.Published && r.YouTubeUrl is not null)
            .Select(r => r.YouTubeUrl![(r.YouTubeUrl!.LastIndexOf('/') + 1)..]).Where(id => Regex.IsMatch(id, "^[A-Za-z0-9_-]{6,20}$")).Distinct().ToList();

    public static string Message(IReadOnlyList<VideoStats> stats, DateTimeOffset now)
    {
        if (stats.Count == 0) return "📊 Nenhum vídeo publicado ainda.";
        // Brazilian number format: 12.345.
        static string N(double n) => n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
        var text = new StringBuilder($"📊 {stats.Count} vídeo(s) · {N(stats.Sum(s => s.Views))} visualizações · {N(stats.Sum(s => s.Likes))} curtidas · {N(stats.Sum(s => s.Comments))} comentários\n");
        foreach (var s in stats.OrderByDescending(s => s.Views).Take(15))
        {
            var days = Math.Max(1, (now - s.PublishedAt).TotalDays);
            text.Append($"\n• {TextUtil.Truncate(s.Title, 60)}: {N(s.Views)} views ({N(s.Views / days)}/dia), {N(s.Likes)} 👍, {N(s.Comments)} 💬");
        }
        return text.ToString();
    }
}
